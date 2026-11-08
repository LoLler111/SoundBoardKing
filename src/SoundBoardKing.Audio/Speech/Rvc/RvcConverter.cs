using System.Reflection;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using SoundBoardKing.Audio.Editing;

namespace SoundBoardKing.Audio.Speech.Rvc;

/// <summary>
/// How an RVC voice changes a voice: pitch in semitones, how much of the index, consonant protection, and how
/// much the melody of the voice moves around its average (1 as it is).
/// </summary>
public sealed record RvcSettings(int Pitch = 0, float IndexRate = 0.66f, float Protect = 0.33f, float RmsMix = 0.25f,
    float Intonation = 1f);

/// <summary>
/// Retrieval-based Voice Conversion (RVC v2), as the reference pipeline does it, without Python: the voice
/// at 16 kHz is high-passed, its pitch read by RMVPE and its content by ContentVec (layer 12); the content
/// is pulled towards the model's own index, and the model's generator speaks it with the target voice.
/// ContentVec and the generator are weight-less ONNX graphs shipped with the app, fed the weights read
/// (safely, see <see cref="TorchCheckpoint"/>) from the user's .pth and from the downloaded ContentVec.
/// </summary>
internal sealed class RvcConverter : IDisposable
{
    public const int InputRate = 16000;
    private const int Hop = 160; // 100 pitch frames per second
    private const int Pad = InputRate; // a second of reflected audio on both sides, as RVC on the CPU (x_pad 1)
    private const int MaxChunkSeconds = 30;

    // scipy.signal.butter(5, 48, "high", fs=16000) and its lfilter_zi: RVC removes rumble below 48 Hz.
    private static readonly double[] HighPassB = [0.9699606451838447, -4.849803225919223, 9.699606451838447, -9.699606451838447, 4.849803225919223, -0.9699606451838447];
    private static readonly double[] HighPassA = [1.0, -4.939001819168364, 9.757863526739543, -9.639544849413458, 4.761506797356209, -0.9408236532054606];
    private static readonly double[] HighPassZi = [-0.9699605304939276, 3.8798421289715876, -5.819763203738301, 3.879842142541546, -0.9699605372808581];

    private readonly WeightedSession _contentVec;
    private readonly InferenceSession _rmvpe;
    private readonly WeightedSession _generator;
    private readonly FaissIndex? _index;
    private readonly float[,] _melBasis = MelBasis();
    private readonly Fft _fft = new(1024);

    /// <exception cref="InvalidDataException">The model is not an RVC v2 48 kHz model with pitch, or a file is damaged.</exception>
    /// <param name="cacheFolder">Where the models, made whole with their weights, are kept between runs.</param>
    /// <param name="gpu">Run ContentVec, RMVPE and the generator on the graphics card when possible.</param>
    public RvcConverter(string contentVecWeights, string rmvpeModel, string modelPath, string? indexPath, string cacheFolder,
        bool gpu = false)
    {
        string? problem = RvcModels.Check(modelPath);
        if (problem is not null)
            throw new InvalidDataException(problem);

        try
        {
            _generator = new WeightedSession("rvc-v2-48k-f0", "net.", modelPath,
                () => TorchCheckpoint.Tensors(((Dictionary<object, object?>)TorchCheckpoint.Load(modelPath)!).GetValueOrDefault("weight")),
                cacheFolder, gpu);
            _contentVec = new WeightedSession("contentvec-layer12", "model.", contentVecWeights,
                () => TorchCheckpoint.Tensors(TorchCheckpoint.Load(contentVecWeights)), cacheFolder, gpu);
            _rmvpe = OnnxSessions.Create(rmvpeModel, gpu, out bool rmvpeOnGpu);
            if (gpu)
            {
                CpuParts = new[] { ("ContentVec", _contentVec.OnGpu), ("RMVPE", rmvpeOnGpu), ("il modello RVC", _generator.OnGpu) }
                    .Where(p => !p.Item2).Select(p => p.Item1).ToList();
            }
            OnGpu = gpu && CpuParts.Count == 0;
            _index = indexPath is not null && File.Exists(indexPath) ? new FaissIndex(indexPath) : null;
            if (_index is not null && _index.Dimension != 768)
                throw new InvalidDataException("L'indice non è di un modello RVC v2 (768 valori per fotogramma).");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public const int OutputRate = 48000;

    /// <summary>Changes the voice of <paramref name="audio"/> (16 kHz mono) into the model's; the result is 48 kHz.</summary>
    public float[] Convert(float[] audio, RvcSettings settings, CancellationToken cancellation)
    {
        var output = new List<float>();
        foreach (var chunk in Chunks(audio))
        {
            cancellation.ThrowIfCancellationRequested();
            output.AddRange(ConvertChunk(chunk, settings, cancellation));
        }
        return [.. output];
    }

    // Long texts in pieces of up to 30 s, cut where the voice is quietest, as RVC does past 41 s.
    private static IEnumerable<float[]> Chunks(float[] audio)
    {
        int max = MaxChunkSeconds * InputRate;
        int start = 0;
        while (audio.Length - start > max)
        {
            int searchFrom = start + max - 5 * InputRate;
            int window = InputRate / 50;
            int cut = start + max;
            double quietest = double.MaxValue;
            for (int at = searchFrom; at + window < start + max; at += window / 2)
            {
                double energy = 0;
                for (int i = at; i < at + window; i++)
                    energy += audio[i] * audio[i];
                if (energy < quietest)
                {
                    quietest = energy;
                    cut = at + window / 2;
                }
            }
            yield return audio[start..cut];
            start = cut;
        }
        yield return audio[start..];
    }

    private float[] ConvertChunk(float[] chunk, RvcSettings settings, CancellationToken cancellation)
    {
        double[] audio = FiltFilt(chunk);
        float[] padded = ReflectPad(audio, Pad);
        int pitchFrames = padded.Length / Hop;

        // Pitch with RMVPE, then shifted, as continuous Hz and as the coarse 1..255 the model embeds.
        float[] f0 = Pitch(padded);
        Array.Resize(ref f0, Math.Min(f0.Length, pitchFrames));
        ScaleIntonation(f0, settings.Intonation, Pad / Hop);
        float shift = MathF.Pow(2, settings.Pitch / 12f);
        for (int i = 0; i < f0.Length; i++)
            f0[i] *= shift;
        cancellation.ThrowIfCancellationRequested();

        // Content with ContentVec (50 frames a second), pulled towards the model's index.
        float[] content = Content(padded, out int frames);
        float[] original = (float[])content.Clone();
        if (_index is not null && settings.IndexRate > 0)
            _index.Blend(content, frames, settings.IndexRate);
        cancellation.ThrowIfCancellationRequested();

        // Twice as many content frames, to match the pitch; unvoiced frames keep part of the original content.
        int length = Math.Min(pitchFrames, Math.Min(frames * 2, f0.Length));
        var phone = new float[length * 768];
        for (int i = 0; i < length; i++)
        {
            int source = i / 2 * 768;
            float keep = f0[i] > 0 ? 1 : settings.Protect;
            for (int j = 0; j < 768; j++)
                phone[i * 768 + j] = content[source + j] * keep + original[source + j] * (1 - keep);
        }
        var coarse = new long[length];
        var pitchf = new float[length];
        double melMin = 1127 * Math.Log(1 + 50 / 700.0);
        double melMax = 1127 * Math.Log(1 + 1100 / 700.0);
        for (int i = 0; i < length; i++)
        {
            pitchf[i] = f0[i];
            double mel = 1127 * Math.Log(1 + f0[i] / 700.0);
            if (mel > 0)
                mel = (mel - melMin) * 254 / (melMax - melMin) + 1;
            coarse[i] = (long)Math.Round(Math.Clamp(mel, 1, 255));
        }
        var noise = new float[192 * length];
        var random = new Random();
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()));

        using var phoneValue = OrtValue.CreateTensorValueFromMemory(phone, [1, length, 768]);
        using var lengthValue = OrtValue.CreateTensorValueFromMemory(new long[] { length }, [1]);
        using var pitchValue = OrtValue.CreateTensorValueFromMemory(coarse, [1, length]);
        using var pitchfValue = OrtValue.CreateTensorValueFromMemory(pitchf, [1, length]);
        using var speakerValue = OrtValue.CreateTensorValueFromMemory(new long[] { 0 }, [1]);
        using var noiseValue = OrtValue.CreateTensorValueFromMemory(noise, [1, 192, length]);
        float[] voiced = _generator.Run(
            ["phone", "phone_lengths", "pitch", "nsff0", "sid", "rnd"],
            [phoneValue, lengthValue, pitchValue, pitchfValue, speakerValue, noiseValue], out _);

        // Without the padding (a second at 48 kHz on each side), then the loudness of the original mixed back in.
        int trim = OutputRate * Pad / InputRate;
        float[] result = voiced.Length > 2 * trim ? voiced[trim..^trim] : [];
        if (settings.RmsMix != 1 && result.Length > 0)
            MixLoudness(audio, result, settings.RmsMix);
        return result;
    }

    /// <summary>
    /// Widens or narrows the melody: every voiced frame's distance from the average pitch (in semitones, i.e. in
    /// log Hz) times <paramref name="scale"/>. The average comes from the real audio, not the padding around it.
    /// </summary>
    internal static void ScaleIntonation(float[] f0, float scale, int padFrames)
    {
        if (Math.Abs(scale - 1) < 0.001f)
            return;
        double sum = 0;
        int count = 0;
        for (int i = padFrames; i < f0.Length - padFrames; i++)
        {
            if (f0[i] > 0)
            {
                sum += Math.Log(f0[i]);
                count++;
            }
        }
        if (count == 0)
            return;
        double average = sum / count;
        for (int i = 0; i < f0.Length; i++)
        {
            if (f0[i] > 0)
                f0[i] = (float)Math.Clamp(Math.Exp(average + (Math.Log(f0[i]) - average) * scale), 50, 1100);
        }
    }

    /// <summary>ContentVec's layer 12, frames x 768.</summary>
    internal float[] Content(float[] audio, out int frames)
    {
        float[] content = _contentVec.Run("source", audio, [1, audio.Length], out var shape);
        frames = (int)shape[1];
        return content;
    }

    internal FaissIndex? Index => _index;

    // ---- Pitch: log-mel spectrogram -> RMVPE -> cents ----------------------------------------------------

    internal float[] Pitch(float[] audio)
    {
        float[] mel = LogMel(audio, out int frames);
        int padded = 32 * ((frames - 1) / 32 + 1);
        var input = new float[128 * padded];
        for (int m = 0; m < 128; m++)
        {
            for (int t = 0; t < padded; t++)
                input[m * padded + t] = mel[m * frames + Reflect(t, frames)];
        }
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 128, padded]);
        using var outputs = _rmvpe.Run(new RunOptions(), ["input"], [value], [_rmvpe.OutputNames[0]]);
        var salience = outputs[0].GetTensorDataAsSpan<float>();

        var f0 = new float[frames];
        for (int t = 0; t < frames; t++)
        {
            var row = salience.Slice(t * 360, 360);
            int center = 0;
            for (int i = 1; i < 360; i++)
            {
                if (row[i] > row[center])
                    center = i;
            }
            if (row[center] <= 0.03f)
                continue;
            double product = 0, weight = 0;
            for (int i = Math.Max(0, center - 4); i <= Math.Min(359, center + 4); i++)
            {
                product += row[i] * (20.0 * i + 1997.3794084376191);
                weight += row[i];
            }
            double hz = 10 * Math.Pow(2, product / weight / 1200);
            f0[t] = hz is < 50 or > 1100 ? 0 : (float)hz;
        }
        return f0;
    }

    /// <summary>log(max(1e-5, mel)), 128 x frames, as torch.stft (centered, reflected, periodic Hann) and librosa's HTK mel.</summary>
    private float[] LogMel(float[] audio, out int frames)
    {
        const int n = 1024;
        frames = 1 + audio.Length / Hop;
        var mel = new float[128 * frames];
        var re = new double[n];
        var im = new double[n];
        var magnitude = new double[n / 2 + 1];
        for (int t = 0; t < frames; t++)
        {
            int start = t * Hop - n / 2;
            for (int i = 0; i < n; i++)
            {
                double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
                re[i] = audio[Reflect(start + i, audio.Length)] * window;
                im[i] = 0;
            }
            _fft.Transform(re, im, inverse: false);
            for (int k = 0; k <= n / 2; k++)
                magnitude[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            for (int m = 0; m < 128; m++)
            {
                double sum = 0;
                for (int k = 0; k <= n / 2; k++)
                    sum += _melBasis[m, k] * magnitude[k];
                mel[m * frames + t] = (float)Math.Log(Math.Max(1e-5, sum));
            }
        }
        return mel;
    }

    /// <summary>librosa.filters.mel(sr=16000, n_fft=1024, n_mels=128, fmin=30, fmax=8000, htk=True), Slaney-normalized.</summary>
    private static float[,] MelBasis()
    {
        const int bins = 513, mels = 128;
        static double ToMel(double hz) => 2595 * Math.Log10(1 + hz / 700);
        static double ToHz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);
        var points = new double[mels + 2];
        double low = ToMel(30), high = ToMel(8000);
        for (int i = 0; i < points.Length; i++)
            points[i] = ToHz(low + (high - low) * i / (mels + 1));
        var basis = new float[mels, bins];
        for (int m = 0; m < mels; m++)
        {
            double norm = 2 / (points[m + 2] - points[m]);
            for (int k = 0; k < bins; k++)
            {
                double hz = 8000.0 * k / (bins - 1);
                double lower = (hz - points[m]) / (points[m + 1] - points[m]);
                double upper = (points[m + 2] - hz) / (points[m + 2] - points[m + 1]);
                basis[m, k] = (float)(Math.Max(0, Math.Min(lower, upper)) * norm);
            }
        }
        return basis;
    }

    // ---- Signal helpers -----------------------------------------------------------------------------------

    /// <summary>scipy.signal.filtfilt with odd extension, as RVC filters its input.</summary>
    internal static double[] FiltFilt(float[] input)
    {
        const int edge = 18; // 3 * max(len(a), len(b))
        int n = input.Length;
        if (n <= edge)
            return input.Select(x => (double)x).ToArray();
        var extended = new double[n + 2 * edge];
        for (int i = 0; i < edge; i++)
        {
            extended[i] = 2 * input[0] - input[edge - i];
            extended[n + edge + i] = 2 * input[n - 1] - input[n - 2 - i];
        }
        for (int i = 0; i < n; i++)
            extended[edge + i] = input[i];

        Filter(extended, extended[0]);
        Array.Reverse(extended);
        Filter(extended, extended[0]);
        Array.Reverse(extended);
        return extended[edge..(edge + n)];
    }

    private static void Filter(double[] x, double initial)
    {
        var z = HighPassZi.Select(v => v * initial).ToArray();
        for (int i = 0; i < x.Length; i++)
        {
            double input = x[i];
            double y = HighPassB[0] * input + z[0];
            for (int k = 1; k < 5; k++)
                z[k - 1] = HighPassB[k] * input + z[k] - HighPassA[k] * y;
            z[4] = HighPassB[5] * input - HighPassA[5] * y;
            x[i] = y;
        }
    }

    private static float[] ReflectPad(double[] audio, int pad)
    {
        var result = new float[audio.Length + 2 * pad];
        for (int i = 0; i < result.Length; i++)
            result[i] = (float)audio[Reflect(i - pad, audio.Length)];
        return result;
    }

    /// <summary>numpy's "reflect" (the edge sample not repeated), bouncing as often as needed.</summary>
    internal static int Reflect(int index, int length)
    {
        if (length == 1)
            return 0;
        int period = 2 * (length - 1);
        index %= period;
        if (index < 0)
            index += period;
        return index < length ? index : period - index;
    }

    /// <summary>RVC's change_rms: the loudness envelope (one point per half second) of the source, mixed back.</summary>
    internal static void MixLoudness(double[] source, float[] output, float rate)
    {
        float[] sourceRms = Rms(source.Select(x => (float)x).ToArray(), InputRate / 2 * 2, InputRate / 2, output.Length);
        float[] outputRms = Rms(output, OutputRate / 2 * 2, OutputRate / 2, output.Length);
        for (int i = 0; i < output.Length; i++)
            output[i] *= MathF.Pow(sourceRms[i], 1 - rate) * MathF.Pow(Math.Max(outputRms[i], 1e-6f), rate - 1);
    }

    // librosa.feature.rms (centered, zero padded), then linear interpolation to the output length.
    private static float[] Rms(float[] y, int frameLength, int hop, int length)
    {
        int frames = 1 + y.Length / hop;
        var rms = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            int start = f * hop - frameLength / 2;
            for (int i = Math.Max(0, start); i < Math.Min(y.Length, start + frameLength); i++)
                sum += (double)y[i] * y[i];
            rms[f] = Math.Sqrt(sum / frameLength);
        }
        var result = new float[length];
        double scale = (double)frames / length;
        for (int i = 0; i < length; i++)
        {
            double position = Math.Max(0, (i + 0.5) * scale - 0.5);
            int i0 = Math.Min((int)position, frames - 1);
            int i1 = Math.Min(i0 + 1, frames - 1);
            double w = position - i0;
            result[i] = (float)(rms[i0] * (1 - w) + rms[i1] * w);
        }
        return result;
    }

    /// <summary>Whether ContentVec, RMVPE and the generator all run on the graphics card.</summary>
    public bool OnGpu { get; }

    /// <summary>The parts left on the processor although the graphics card was asked for.</summary>
    public IReadOnlyList<string> CpuParts { get; } = [];

    private static byte[] Template(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Rvc." + name)
            ?? throw new InvalidOperationException($"Manca lo schema {name} nell'app.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        _generator?.Dispose();
        _contentVec?.Dispose();
        _rmvpe?.Dispose();
        _index?.Dispose();
    }

    /// <summary>
    /// A weight-less graph made whole with the weights of a checkpoint, named like its state dict: the
    /// graph refers to its weights as external data ("weights.bin"), which is written once, as float32 in
    /// the graph's order, into a cache folder beside it. ONNX Runtime then loads it like any model (onto
    /// the graphics card too), and the next time the checkpoint need not even be read.
    /// </summary>
    private sealed class WeightedSession : IDisposable
    {
        private readonly InferenceSession _session;

        /// <param name="template">Name of the embedded graph and of its list of weights ("name&lt;TAB&gt;dims").</param>
        /// <param name="source">The checkpoint file: its name, size and date tell whether the cache is still good.</param>
        /// <param name="tensors">Reads the checkpoint, only when the cache must be (re)written.</param>
        public WeightedSession(string template, string prefix, string source, Func<Dictionary<string, TorchTensor>> tensors,
            string cacheRoot, bool gpu)
        {
            string folder = CacheFolder(template, source, cacheRoot);
            string model = Path.Combine(folder, template + ".onnx");
            if (!File.Exists(Path.Combine(folder, "complete")))
                Write(template, prefix, tensors(), folder);
            _session = OnnxSessions.Create(model, gpu, out bool onGpu);
            OnGpu = onGpu;
        }

        public bool OnGpu { get; }

        private static string CacheFolder(string template, string source, string cacheRoot)
        {
            var info = new FileInfo(source);
            string identity = $"{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            string hash = System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
            return Path.Combine(cacheRoot, $"{template}-{hash}");
        }

        private static void Write(string template, string prefix, Dictionary<string, TorchTensor> tensors, string folder)
        {
            string staging = folder + ".new";
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            using (var weights = new BinaryWriter(File.Create(Path.Combine(staging, "weights.bin"))))
            {
                foreach (var line in Encoding.UTF8.GetString(Template(template + ".weights.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\t');
                    string key = parts[0][prefix.Length..];
                    long[] dims = parts.Length > 1 && parts[1].Length > 0 ? parts[1].Split(',').Select(long.Parse).ToArray() : [];
                    if (!tensors.TryGetValue(key, out var tensor))
                        throw new InvalidDataException($"Nel modello manca il peso {key}: non è un modello RVC v2 compatibile.");
                    if (!dims.SequenceEqual(tensor.Shape))
                        throw new InvalidDataException($"Il peso {key} ha una forma diversa da quella attesa: modello non compatibile.");
                    weights.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tensor.Data.AsSpan()));
                }
            }
            File.WriteAllBytes(Path.Combine(staging, template + ".onnx"), Template(template + ".onnx"));
            File.WriteAllText(Path.Combine(staging, "complete"), "");
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.Move(staging, folder);
        }

        public float[] Run(string name, float[] data, long[] shape, out long[] outputShape)
        {
            using var value = OrtValue.CreateTensorValueFromMemory(data, shape);
            return Run([name], [value], out outputShape);
        }

        public float[] Run(string[] names, OrtValue[] values, out long[] outputShape)
        {
            using var outputs = _session.Run(new RunOptions(), names, values, [_session.OutputNames[0]]);
            outputShape = outputs[0].GetTensorTypeAndShape().Shape;
            return outputs[0].GetTensorDataAsSpan<float>().ToArray();
        }

        public void Dispose() => _session.Dispose();
    }
}
