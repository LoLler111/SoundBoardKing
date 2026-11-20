using System.Reflection;
using Microsoft.ML.OnnxRuntime;

namespace SoundBoardKing.Audio.Editing;

/// <summary>Two voices that talked over each other, each at the clip's own rate and channels.</summary>
/// <param name="Main">The voice heard the most.</param>
/// <param name="Other">The rest of the speech.</param>
public sealed record SeparatedVoices(AudioClip Main, AudioClip Other);

/// <summary>
/// Splits a voice track with two people talking into one track per person, with TIGER-speech (Kai Li et al.,
/// ICLR 2025, trained on noisy and echoing mixes; code MIT, weights Apache 2.0). The network ships with the
/// app (7 MB): only its spectrum-to-spectra part, so the STFT is done here as for the voice model.
/// <para>
/// It works on 16 kHz mono in 4 s chunks overlapping by 1 s. The model does not know who is who, so at each
/// overlap the next chunk's two outputs are matched to the previous ones. The result is then laid back on
/// the original sound: below 7.6 kHz the model's voices, plus their share of the stereo sides; above it,
/// where the model hears nothing, the original split frame by frame as the top of the voices is. The two
/// always add up to the original.
/// </para>
/// Music under the voices is shared out between them: it goes first, with <see cref="VoiceSeparator"/>.
/// </summary>
public static class SpeakerSeparator
{
    internal const int ModelRate = 16000;
    private const int NFft = 640;
    private const int Hop = 160;
    private const int Frames = 400; // fixed: the graph was exported for this many STFT frames
    private const int Bins = NFft / 2 + 1;
    internal const int ChunkSize = Hop * (Frames - 1);
    internal const int OverlapSize = ModelRate;

    // Above this the model's output has nothing (its Nyquist is 8 kHz, minus the resampler's slope).
    private const double ModelTopHz = 7600;
    private const double ShareFromHz = 4500;

    private static readonly Lock SessionGate = new();
    private static InferenceSession? _session;

    /// <summary>Separates the clip; <paramref name="progress"/> gets 0..1.</summary>
    public static SeparatedVoices Separate(AudioClip clip, IProgress<double>? progress, CancellationToken cancel)
    {
        var mono = clip.ConvertTo(ModelRate, 1).Samples;
        var (a, b) = Demix(mono, progress, cancel);
        if (Energy(b) > Energy(a))
            (a, b) = (b, a);

        if (clip.SampleRate <= ModelRate)
        {
            return new SeparatedVoices(
                new AudioClip(a, ModelRate, 1).ConvertTo(clip.SampleRate, clip.Channels),
                new AudioClip(b, ModelRate, 1).ConvertTo(clip.SampleRate, clip.Channels));
        }
        var (main, other) = OnOriginal(clip, a, b, cancel);
        return new SeparatedVoices(main, other);
    }

    /// <summary>The model's two outputs at 16 kHz, in no particular order; together they make the input back.</summary>
    internal static (float[] A, float[] B) Demix(float[] mono, IProgress<double>? progress, CancellationToken cancel)
    {
        int length = mono.Length;
        var outA = new float[length];
        var outB = new float[length];
        if (length == 0)
            return (outA, outB);

        int step = ChunkSize - OverlapSize;
        int chunks = length <= ChunkSize ? 1 : 1 + (length - ChunkSize + step - 1) / step;

        var session = Session();
        string inputName = session.InputNames[0];
        string outputName = session.OutputNames[0];
        var stft = new StereoStft(NFft, Hop);
        var spec = new float[2 * Bins * Frames];
        long[] shape = [1, 2, Bins, Frames];
        double[] re = new double[Bins], im = new double[Bins], unusedRe = new double[Bins], unusedIm = new double[Bins];
        double[] aRe = new double[Bins], aIm = new double[Bins], bRe = new double[Bins], bIm = new double[Bins];
        var chunk = new float[ChunkSize];
        float[]? previousA = null, previousB = null;

        for (int c = 0; c < chunks; c++)
        {
            cancel.ThrowIfCancellationRequested();
            int start = c * step;
            int actual = Math.Min(ChunkSize, length - start);
            Array.Clear(chunk);
            Array.Copy(mono, start, chunk, 0, actual);

            for (int t = 0; t < Frames; t++)
            {
                stft.Analyze(chunk, chunk, ChunkSize, t, re, im, unusedRe, unusedIm);
                for (int f = 0; f < Bins; f++)
                {
                    spec[f * Frames + t] = (float)re[f];
                    spec[(Bins + f) * Frames + t] = (float)im[f];
                }
            }

            float[] predicted;
            using (var runOptions = new RunOptions())
            using (var input = OrtValue.CreateTensorValueFromMemory(spec, shape))
            using (var outputs = session.Run(runOptions, [inputName], [input], [outputName]))
                predicted = outputs[0].GetTensorDataAsSpan<float>().ToArray();

            // Output [1, source, re/im, bin, frame]: source A goes through the "left" channel, B the "right".
            var synthesis = stft.StartSynthesis(Frames);
            for (int t = 0; t < Frames; t++)
            {
                for (int f = 0; f < Bins; f++)
                {
                    aRe[f] = predicted[(0 * Bins + f) * Frames + t];
                    aIm[f] = predicted[(1 * Bins + f) * Frames + t];
                    bRe[f] = predicted[(2 * Bins + f) * Frames + t];
                    bIm[f] = predicted[(3 * Bins + f) * Frames + t];
                }
                synthesis.Add(t, aRe, aIm, bRe, bIm);
            }
            var (chunkA, chunkB) = synthesis.Finish(ChunkSize);

            if (previousA is not null && ShouldSwap(previousA, previousB!, chunkA, chunkB, step))
                (chunkA, chunkB) = (chunkB, chunkA);

            bool last = c == chunks - 1;
            for (int i = 0; i < actual; i++)
            {
                double w = 1;
                if (c > 0 && i < OverlapSize)
                    w = Square(Math.Sin(Math.PI / 2 * (i + 0.5) / OverlapSize));
                else if (!last && i >= step)
                    w = Square(Math.Cos(Math.PI / 2 * (i - step + 0.5) / OverlapSize));
                outA[start + i] += (float)(chunkA[i] * w);
                outB[start + i] += (float)(chunkB[i] * w);
            }
            (previousA, previousB) = (chunkA, chunkB);
            progress?.Report((double)(c + 1) / chunks);
        }
        return (outA, outB);
    }

    // Over the second the two chunks share, does the new A sound like the old B?
    internal static bool ShouldSwap(float[] previousA, float[] previousB, float[] a, float[] b, int offset)
    {
        double same = 0, swapped = 0;
        for (int i = 0; i < OverlapSize; i++)
        {
            double pa = previousA[offset + i], pb = previousB[offset + i];
            same += pa * a[i] + pb * b[i];
            swapped += pa * b[i] + pb * a[i];
        }
        return swapped > same;
    }

    /// <summary>
    /// Each voice as a mask on the original: below 7.6 kHz the share of the two voices' power in each bin,
    /// above it (where the model hears nothing) each voice's share of the top of its band, frame by frame.
    /// </summary>
    private static (AudioClip Main, AudioClip Other) OnOriginal(AudioClip clip, float[] a, float[] b, CancellationToken cancel)
    {
        int rate = clip.SampleRate;
        var stereo = clip.ConvertTo(rate, 2);
        var (left, right) = VoiceSeparator.Split(stereo);
        int length = left.Length;
        // Back at the clip's rate, lined up with it: the two resamplings delay by a few samples, which at the
        // top of the band would turn into a phase error.
        var upA = new AudioClip(a, ModelRate, 1).ConvertTo(rate, 1).Samples;
        var upB = new AudioClip(b, ModelRate, 1).ConvertTo(rate, 1).Samples;
        int lag = Delay(left, right, upA, upB);
        upA = Fit(upA, lag, length);
        upB = Fit(upB, lag, length);

        int nFft = 512;
        while (nFft < rate / 25) // about 40 ms, as the model's own frames
            nFft <<= 1;
        var stft = new StereoStft(nFft, nFft / 4);
        int frames = stft.FrameCount(length);
        int bins = stft.Bins;
        int top = Math.Min(bins, (int)(ModelTopHz * nFft / rate));
        int shareFrom = (int)(ShareFromHz * nFft / rate);

        double[] lr = new double[bins], li = new double[bins], rr = new double[bins], ri = new double[bins];
        double[] ar = new double[bins], ai = new double[bins], br = new double[bins], bi = new double[bins];
        double[] mlr = new double[bins], mli = new double[bins], mrr = new double[bins], mri = new double[bins];
        double[] olr = new double[bins], oli = new double[bins], orr = new double[bins], ori = new double[bins];
        var mainSynthesis = stft.StartSynthesis(frames);
        var otherSynthesis = stft.StartSynthesis(frames);
        double share = 0.5;

        for (int t = 0; t < frames; t++)
        {
            if ((t & 63) == 0)
                cancel.ThrowIfCancellationRequested();
            stft.Analyze(left, right, length, t, lr, li, rr, ri);
            stft.Analyze(upA, upB, length, t, ar, ai, br, bi);

            double highA = 0, highB = 0;
            for (int k = shareFrom; k < top; k++)
            {
                highA += ar[k] * ar[k] + ai[k] * ai[k];
                highB += br[k] * br[k] + bi[k] * bi[k];
            }
            double frameShare = highA + highB > 1e-12 ? highA / (highA + highB) : 0.5;
            share = 0.5 * share + 0.5 * frameShare;

            for (int k = 0; k < bins; k++)
            {
                if (k < top)
                {
                    // The model's own voice A, plus its share of what the mono model could not account for
                    // (the stereo sides, the resampler's slope near the top).
                    double pa = ar[k] * ar[k] + ai[k] * ai[k], pb = br[k] * br[k] + bi[k] * bi[k];
                    double mask = pa + pb > 1e-14 ? pa / (pa + pb) : 0.5;
                    double sumRe = ar[k] + br[k], sumIm = ai[k] + bi[k];
                    mlr[k] = ar[k] + mask * (lr[k] - sumRe);
                    mli[k] = ai[k] + mask * (li[k] - sumIm);
                    mrr[k] = ar[k] + mask * (rr[k] - sumRe);
                    mri[k] = ai[k] + mask * (ri[k] - sumIm);
                }
                else
                {
                    mlr[k] = lr[k] * share; mli[k] = li[k] * share; mrr[k] = rr[k] * share; mri[k] = ri[k] * share;
                }
                olr[k] = lr[k] - mlr[k]; oli[k] = li[k] - mli[k]; orr[k] = rr[k] - mrr[k]; ori[k] = ri[k] - mri[k];
            }
            mainSynthesis.Add(t, mlr, mli, mrr, mri);
            otherSynthesis.Add(t, olr, oli, orr, ori);
        }
        return (ToClip(mainSynthesis.Finish(length), rate, clip.Channels), ToClip(otherSynthesis.Finish(length), rate, clip.Channels));
    }

    private static AudioClip ToClip((float[] Left, float[] Right) signal, int rate, int channels)
    {
        var (left, right) = signal;
        var samples = new float[left.Length * 2];
        for (int i = 0; i < left.Length; i++)
        {
            samples[i * 2] = left[i];
            samples[i * 2 + 1] = right[i];
        }
        return new AudioClip(samples, rate, 2).ConvertTo(rate, channels);
    }

    // How many samples the voices (resampled twice) lag behind the original: the best match within ±64,
    // on up to 20 s of the sound.
    internal static int Delay(float[] left, float[] right, float[] a, float[] b)
    {
        const int range = 64;
        int n = Math.Min(Math.Min(left.Length, a.Length), 20 * 48000);
        int best = 0;
        double bestScore = double.MinValue;
        for (int lag = -range; lag <= range; lag++)
        {
            double score = 0;
            for (int i = range; i < n - range; i++)
                score += (left[i] + right[i]) * (a[i + lag] + b[i + lag]);
            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }
        return best;
    }

    // The samples from index lag on, cut or padded to length.
    private static float[] Fit(float[] samples, int lag, int length)
    {
        var fitted = new float[length];
        for (int i = 0; i < length; i++)
        {
            int j = i + lag;
            if (j >= 0 && j < samples.Length)
                fitted[i] = samples[j];
        }
        return fitted;
    }

    private static double Energy(float[] samples)
    {
        double sum = 0;
        foreach (float s in samples)
            sum += s * s;
        return sum;
    }

    private static double Square(double x) => x * x;

    /// <summary>Frees the model until it is needed again. Not while a separation runs.</summary>
    public static void Release()
    {
        lock (SessionGate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    private static InferenceSession Session()
    {
        lock (SessionGate)
        {
            if (_session is null)
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Editing.tiger-speech.onnx")
                    ?? throw new InvalidOperationException("Manca il modello per separare le voci nell'app.");
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                _session = new InferenceSession(memory.ToArray(), options);
            }
            return _session;
        }
    }
}
