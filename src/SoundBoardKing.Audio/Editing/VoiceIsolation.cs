using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace SoundBoardKing.Audio.Editing;

/// <summary>What voice isolation keeps.</summary>
public enum VoicePart
{
    /// <summary>The voice alone: music, effects and noise go.</summary>
    Voice,

    /// <summary>Everything but the voice (karaoke, a film's background).</summary>
    Background,
}

/// <summary>
/// The separation model, downloaded the first time it is needed: UVR-MDX-NET Voc_FT from Ultimate Vocal
/// Remover's public models (MIT). Kept in the local app data, not next to the app, so updates leave it.
/// </summary>
public static class VoiceModel
{
    public const string FileName = "UVR-MDX-NET-Voc_FT.onnx";
    public const long SizeBytes = 66_762_490;
    private const string Sha256 = "534b2070fcc7df514b13ef660dc8cbb328679c2374d04354a5c42bb14ecce111";
    private static readonly Uri Source = new("https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/" + FileName);

    public static string FolderPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundBoardKing", "Models");

    public static string FilePath => Path.Combine(FolderPath, FileName);

    public static bool IsInstalled => File.Exists(FilePath) && new FileInfo(FilePath).Length == SizeBytes;

    /// <summary>Downloads and checks the model; <paramref name="progress"/> gets 0..1.</summary>
    /// <exception cref="HttpRequestException">No connection, or the file is gone.</exception>
    /// <exception cref="InvalidDataException">The download is not the expected file.</exception>
    public static async Task DownloadAsync(HttpClient http, IProgress<double>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(FolderPath);
        var partial = FilePath + ".part";
        try
        {
            using (var response = await http.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? SizeBytes;
                await using var input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    progress?.Report(Math.Min(1, (double)done / total));
                }
            }

            await using (var check = File.OpenRead(partial))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancel));
                if (hash != Sha256)
                    throw new InvalidDataException("Il modello scaricato non è quello atteso: riprova più tardi.");
            }
            File.Move(partial, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }
}

/// <summary>
/// Separates the voice from the rest with the MDX-Net model, on the CPU. The sound is brought to
/// 44.1 kHz stereo (what the model knows), cut in overlapping chunks of about 6 s and put back
/// together with Hann cross-fades, as Ultimate Vocal Remover does.
/// </summary>
public static class VoiceSeparator
{
    private const int ModelRate = 44100;
    private const int NFft = 7680;
    private const int Hop = 1024;
    private const int DimF = 3072;
    private const int DimT = 256;
    private const int ChunkSize = Hop * (DimT - 1);
    private const int Trim = NFft / 2;
    private const double Overlap = 0.25;

    // The model's output is a little quiet: Voc_FT's correction before subtracting it from the mix.
    private const float Compensate = 1.021f;

    private static readonly Lock SessionGate = new();
    private static InferenceSession? _session;

    /// <summary>Keeps <paramref name="part"/> of the clip; <paramref name="progress"/> gets 0..1.</summary>
    /// <exception cref="FileNotFoundException">The model is not downloaded yet.</exception>
    public static AudioClip Isolate(AudioClip clip, VoicePart part, IProgress<double>? progress, CancellationToken cancel)
    {
        if (!VoiceModel.IsInstalled)
            throw new FileNotFoundException("Il modello per isolare la voce non è ancora scaricato.", VoiceModel.FilePath);

        var stereo = clip.ConvertTo(ModelRate, 2);
        int length = stereo.Frames;
        var (left, right) = Split(stereo);
        var (voiceLeft, voiceRight) = Demix(left, right, progress, cancel);

        var result = new float[length * 2];
        for (int i = 0; i < length; i++)
        {
            float l = voiceLeft[i], r = voiceRight[i];
            if (part == VoicePart.Background)
            {
                l = left[i] - l * Compensate;
                r = right[i] - r * Compensate;
            }
            result[i * 2] = l;
            result[i * 2 + 1] = r;
        }
        return new AudioClip(result, ModelRate, 2).ConvertTo(clip.SampleRate, clip.Channels);
    }

    private static (float[] Voice0, float[] Voice1) Demix(float[] left, float[] right, IProgress<double>? progress, CancellationToken cancel)
    {
        int length = left.Length;
        int genSize = ChunkSize - 2 * Trim;
        int pad = genSize + Trim - length % genSize;
        int total = Trim + length + pad;
        var mixLeft = new float[total];
        var mixRight = new float[total];
        Array.Copy(left, 0, mixLeft, Trim, length);
        Array.Copy(right, 0, mixRight, Trim, length);

        var outLeft = new double[total];
        var outRight = new double[total];
        var divider = new double[total];
        int step = (int)((1 - Overlap) * ChunkSize);
        int chunks = (total + step - 1) / step;

        var session = Session();
        string inputName = session.InputNames[0];
        string outputName = session.OutputNames[0];
        var spec = new float[4 * DimF * DimT];
        var stft = new StereoStft(NFft, Hop);
        var bins = new double[4][];
        for (int i = 0; i < 4; i++)
            bins[i] = new double[stft.Bins];
        var chunkLeft = new float[ChunkSize];
        var chunkRight = new float[ChunkSize];
        long[] shape = [1, 4, DimF, DimT];

        int done = 0;
        for (int start = 0; start < total; start += step)
        {
            cancel.ThrowIfCancellationRequested();
            int end = Math.Min(start + ChunkSize, total);
            int actual = end - start;
            Array.Clear(chunkLeft);
            Array.Clear(chunkRight);
            Array.Copy(mixLeft, start, chunkLeft, 0, actual);
            Array.Copy(mixRight, start, chunkRight, 0, actual);

            // Spectrum in the model's layout: [left re, left im, right re, right im] x frequency x time.
            for (int t = 0; t < DimT; t++)
            {
                stft.Analyze(chunkLeft, chunkRight, ChunkSize, t, bins[0], bins[1], bins[2], bins[3]);
                for (int c = 0; c < 4; c++)
                {
                    for (int f = 0; f < DimF; f++)
                        spec[(c * DimF + f) * DimT + t] = f < 3 ? 0 : (float)bins[c][f]; // the lowest bins are cut, as in training
                }
            }

            float[] predicted;
            using (var input = OrtValue.CreateTensorValueFromMemory(spec, shape))
            using (var outputs = session.Run(new RunOptions(), [inputName], [input], [outputName]))
                predicted = outputs[0].GetTensorDataAsSpan<float>().ToArray();

            var synthesis = stft.StartSynthesis(DimT);
            for (int c = 0; c < 4; c++)
                Array.Clear(bins[c]);
            for (int t = 0; t < DimT; t++)
            {
                for (int c = 0; c < 4; c++)
                {
                    for (int f = 0; f < DimF; f++)
                        bins[c][f] = predicted[(c * DimF + f) * DimT + t];
                }
                synthesis.Add(t, bins[0], bins[1], bins[2], bins[3]);
            }
            var (voiceLeft, voiceRight) = synthesis.Finish(ChunkSize);

            for (int i = 0; i < actual; i++)
            {
                // np.hanning: symmetric, zero at both ends.
                double w = actual > 1 ? 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (actual - 1)) : 1;
                outLeft[start + i] += voiceLeft[i] * w;
                outRight[start + i] += voiceRight[i] * w;
                divider[start + i] += w;
            }
            progress?.Report((double)++done / chunks);
        }

        var resultLeft = new float[length];
        var resultRight = new float[length];
        for (int i = 0; i < length; i++)
        {
            double d = divider[Trim + i];
            if (d > 1e-8)
            {
                resultLeft[i] = (float)(outLeft[Trim + i] / d);
                resultRight[i] = (float)(outRight[Trim + i] / d);
            }
        }
        return (resultLeft, resultRight);
    }

    /// <summary>Frees the model's memory (a few hundred MB) until it is needed again. Not while a separation runs.</summary>
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
                var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                _session = new InferenceSession(VoiceModel.FilePath, options);
            }
            return _session;
        }
    }

    internal static (float[] Left, float[] Right) Split(AudioClip stereo)
    {
        var left = new float[stereo.Frames];
        var right = new float[stereo.Frames];
        for (int i = 0; i < stereo.Frames; i++)
        {
            left[i] = stereo.Samples[i * 2];
            right[i] = stereo.Samples[i * 2 + 1];
        }
        return (left, right);
    }
}

/// <summary>
/// The quick way, with no model: keeps what sits in the middle of the stereo image (where film
/// dialogue is mixed) and in the voice's frequency band. Music and effects panned to the sides go;
/// what shares the centre with the voice stays.
/// </summary>
public static class CenterVoiceFilter
{
    private const int NFft = 2048;
    private const int Hop = 512;
    private const double LowHz = 90;
    private const double HighHz = 8000;

    public static AudioClip Isolate(AudioClip clip, VoicePart part, CancellationToken cancel)
    {
        var stereo = clip.ConvertTo(clip.SampleRate, 2);
        var (left, right) = VoiceSeparator.Split(stereo);
        int length = left.Length;
        var stft = new StereoStft(NFft, Hop);
        int frames = stft.FrameCount(length);
        var synthesis = stft.StartSynthesis(frames);
        int bins = stft.Bins;
        double[] lr = new double[bins], li = new double[bins], rr = new double[bins], ri = new double[bins];
        bool mono = IsMono(left, right);

        for (int t = 0; t < frames; t++)
        {
            if ((t & 63) == 0)
                cancel.ThrowIfCancellationRequested();
            stft.Analyze(left, right, length, t, lr, li, rr, ri);
            for (int k = 0; k < bins; k++)
            {
                double hz = (double)k * clip.SampleRate / NFft;
                double band = hz < LowHz ? 0 : hz > HighHz ? 0.1 : 1;
                // How alike the two channels are in this bin: 1 for the centre, 0 for one side only.
                double cross = 2 * (lr[k] * rr[k] + li[k] * ri[k]);
                double power = lr[k] * lr[k] + li[k] * li[k] + rr[k] * rr[k] + ri[k] * ri[k];
                double centre = mono ? 1 : power > 1e-12 ? Math.Max(0, cross / power) : 0;
                double keep = band * centre * centre;
                if (part == VoicePart.Background)
                    keep = 1 - keep;
                if (part == VoicePart.Voice)
                {
                    // The centre of both channels: the voice in mono, on both sides.
                    double mr = (lr[k] + rr[k]) / 2 * keep, mi = (li[k] + ri[k]) / 2 * keep;
                    lr[k] = rr[k] = mr;
                    li[k] = ri[k] = mi;
                }
                else
                {
                    lr[k] *= keep;
                    li[k] *= keep;
                    rr[k] *= keep;
                    ri[k] *= keep;
                }
            }
            synthesis.Add(t, lr, li, rr, ri);
        }

        var (outLeft, outRight) = synthesis.Finish(length);
        var result = new float[length * 2];
        for (int i = 0; i < length; i++)
        {
            result[i * 2] = outLeft[i];
            result[i * 2 + 1] = outRight[i];
        }
        return new AudioClip(result, clip.SampleRate, 2).ConvertTo(clip.SampleRate, clip.Channels);
    }

    private static bool IsMono(float[] left, float[] right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            if (Math.Abs(left[i] - right[i]) > 1e-4f)
                return false;
        }
        return true;
    }
}
