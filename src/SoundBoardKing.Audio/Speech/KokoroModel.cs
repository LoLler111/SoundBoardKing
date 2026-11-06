using System.IO.Compression;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SoundBoardKing.Audio.Speech;

/// <summary>
/// Kokoro-82M run with ONNX Runtime, as kokoro-onnx does: the phonemes become token ids (0 at both ends),
/// the voice is a style vector chosen by the number of tokens, long texts go in batches of up to 510.
/// </summary>
internal sealed class KokoroModel : IDisposable
{
    public const int SampleRate = 24000;
    private const int MaxTokens = 510;
    private const float SentencePauseSeconds = 0.25f;
    private const float ClausePauseSeconds = 0.1f;

    // The model's vocabulary (kokoro-onnx config.json); other phonemes are dropped.
    private static readonly Dictionary<char, long> Vocab = new()
    {
        [';'] = 1, [':'] = 2, [','] = 3, ['.'] = 4, ['!'] = 5, ['?'] = 6, ['—'] = 9, ['…'] = 10,
        ['"'] = 11, ['('] = 12, [')'] = 13, ['“'] = 14, ['”'] = 15, [' '] = 16, ['̃'] = 17, ['ʣ'] = 18,
        ['ʥ'] = 19, ['ʦ'] = 20, ['ʨ'] = 21, ['ᵝ'] = 22, ['ꭧ'] = 23, ['A'] = 24, ['I'] = 25, ['O'] = 31,
        ['Q'] = 33, ['S'] = 35, ['T'] = 36, ['W'] = 39, ['Y'] = 41, ['ᵊ'] = 42, ['a'] = 43, ['b'] = 44,
        ['c'] = 45, ['d'] = 46, ['e'] = 47, ['f'] = 48, ['h'] = 50, ['i'] = 51, ['j'] = 52, ['k'] = 53,
        ['l'] = 54, ['m'] = 55, ['n'] = 56, ['o'] = 57, ['p'] = 58, ['q'] = 59, ['r'] = 60, ['s'] = 61,
        ['t'] = 62, ['u'] = 63, ['v'] = 64, ['w'] = 65, ['x'] = 66, ['y'] = 67, ['z'] = 68, ['ɑ'] = 69,
        ['ɐ'] = 70, ['ɒ'] = 71, ['æ'] = 72, ['β'] = 75, ['ɔ'] = 76, ['ɕ'] = 77, ['ç'] = 78, ['ɖ'] = 80,
        ['ð'] = 81, ['ʤ'] = 82, ['ə'] = 83, ['ɚ'] = 85, ['ɛ'] = 86, ['ɜ'] = 87, ['ɟ'] = 90, ['ɡ'] = 92,
        ['ɥ'] = 99, ['ɨ'] = 101, ['ɪ'] = 102, ['ʝ'] = 103, ['ɯ'] = 110, ['ɰ'] = 111, ['ŋ'] = 112, ['ɳ'] = 113,
        ['ɲ'] = 114, ['ɴ'] = 115, ['ø'] = 116, ['ɸ'] = 118, ['θ'] = 119, ['œ'] = 120, ['ɹ'] = 123, ['ɾ'] = 125,
        ['ɻ'] = 126, ['ʁ'] = 128, ['ɽ'] = 129, ['ʂ'] = 130, ['ʃ'] = 131, ['ʈ'] = 132, ['ʧ'] = 133, ['ʊ'] = 135,
        ['ʋ'] = 136, ['ʌ'] = 138, ['ɣ'] = 139, ['ɤ'] = 140, ['χ'] = 142, ['ʎ'] = 143, ['ʒ'] = 147, ['ʔ'] = 148,
        ['ˈ'] = 156, ['ˌ'] = 157, ['ː'] = 158, ['ʰ'] = 162, ['ʲ'] = 164, ['↓'] = 169, ['→'] = 171, ['↗'] = 172,
        ['↘'] = 173, ['ᵻ'] = 177,
    };

    private readonly InferenceSession _session;
    private readonly string _voicesPath;
    private readonly Dictionary<string, float[]> _styles = [];
    private readonly string _tokensInput;
    private readonly bool _integerSpeed;

    public KokoroModel(string modelPath, string voicesPath)
    {
        _voicesPath = voicesPath;
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
        // Older exports call the input "tokens", newer ones "input_ids"; some take the speed as an integer.
        _tokensInput = _session.InputMetadata.ContainsKey("input_ids") ? "input_ids" : "tokens";
        _integerSpeed = _session.InputMetadata["speed"].ElementDataType is TensorElementType.Int32 or TensorElementType.Int64;
    }

    /// <param name="speed">1 normal, 0.5..2.</param>
    public float[] Synthesize(IReadOnlyList<string> sentences, string voice, float speed, CancellationToken cancellation)
    {
        float[] styles = Styles(voice);
        var audio = new List<float>();
        var batches = Batches(sentences);
        for (int b = 0; b < batches.Count; b++)
        {
            cancellation.ThrowIfCancellationRequested();
            long[] tokens = Tokens(batches[b]);
            if (tokens.Length == 0)
                continue;
            if (audio.Count > 0)
            {
                bool sentenceEnd = batches[b - 1].TrimEnd() is var previous && previous.Length > 0 && previous[^1] is '.' or '!' or '?';
                audio.AddRange(new float[(int)(SampleRate * (sentenceEnd ? SentencePauseSeconds : ClausePauseSeconds))]);
            }
            audio.AddRange(Trim(Run(tokens, styles, speed)));
        }
        return [.. audio];
    }

    internal static long[] Tokens(string phonemes) =>
        phonemes.Where(Vocab.ContainsKey).Select(c => Vocab[c]).ToArray();

    /// <summary>Whole sentences together while they fit in 510 tokens; longer ones split at punctuation, then at spaces.</summary>
    internal static List<string> Batches(IReadOnlyList<string> sentences)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in sentences)
        {
            foreach (var piece in Split(sentence))
            {
                if (current.Length > 0 && Tokens(current + " " + piece).Length > MaxTokens)
                {
                    batches.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0)
                    current.Append(' ');
                current.Append(piece);
            }
        }
        if (current.Length > 0)
            batches.Add(current.ToString());
        return batches;
    }

    private static IEnumerable<string> Split(string sentence)
    {
        if (Tokens(sentence).Length <= MaxTokens)
        {
            yield return sentence;
            yield break;
        }
        var piece = new StringBuilder();
        foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (piece.Length > 0 && Tokens(piece + " " + word).Length > MaxTokens)
            {
                yield return piece.ToString();
                piece.Clear();
            }
            if (piece.Length > 0)
                piece.Append(' ');
            // A single "word" longer than a batch cannot happen with real text; cut it anyway.
            piece.Append(word.Length > MaxTokens ? word[..MaxTokens] : word);
        }
        if (piece.Length > 0)
            yield return piece.ToString();
    }

    private float[] Run(long[] tokens, float[] styles, float speed)
    {
        long[] padded = [0, .. tokens, 0];
        // One style per length: n tokens use row n - 1 of the voice's 510 x 256 table.
        int row = Math.Min(tokens.Length, styles.Length / 256) - 1;
        float[] style = styles.AsSpan(row * 256, 256).ToArray();
        speed = Math.Clamp(speed, 0.5f, 2f);
        using var tokenInput = OrtValue.CreateTensorValueFromMemory(padded, [1, padded.Length]);
        using var styleInput = OrtValue.CreateTensorValueFromMemory(style, [1, 256]);
        using var speedInput = _integerSpeed
            ? OrtValue.CreateTensorValueFromMemory(new int[] { Math.Max(1, (int)Math.Round(speed)) }, [1])
            : OrtValue.CreateTensorValueFromMemory(new float[] { speed }, [1]);
        using var outputs = _session.Run(new RunOptions(), [_tokensInput, "style", "speed"],
            [tokenInput, styleInput, speedInput], [_session.OutputNames[0]]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    // The model leaves some silence at both ends of a batch; trimmed so the pauses between batches stay even.
    private static float[] Trim(float[] audio)
    {
        const float threshold = 0.003f;
        int start = 0;
        while (start < audio.Length && Math.Abs(audio[start]) < threshold)
            start++;
        int end = audio.Length;
        while (end > start && Math.Abs(audio[end - 1]) < threshold)
            end--;
        // A little margin, so the first and last sounds are not clipped.
        int margin = SampleRate / 50;
        start = Math.Max(0, start - margin);
        end = Math.Min(audio.Length, end + margin);
        return audio[start..end];
    }

    /// <summary>The 510 x 1 x 256 style table of a voice, read from the voices archive (NumPy .npz).</summary>
    private float[] Styles(string voice)
    {
        if (_styles.TryGetValue(voice, out var cached))
            return cached;
        using var zip = ZipFile.OpenRead(_voicesPath);
        var entry = zip.GetEntry(voice + ".npy")
            ?? throw new InvalidOperationException($"La voce Kokoro \"{voice}\" non c'è nel file delle voci.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return _styles[voice] = ReadNpyFloats(memory.ToArray());
    }

    /// <summary>The data of a little-endian float32 .npy array, whatever its shape.</summary>
    internal static float[] ReadNpyFloats(byte[] npy)
    {
        if (npy.Length < 10 || npy[0] != 0x93 || Encoding.ASCII.GetString(npy, 1, 5) != "NUMPY")
            throw new InvalidDataException("Il file delle voci Kokoro non è valido.");
        int major = npy[6];
        int headerLength = major == 1 ? BitConverter.ToUInt16(npy, 8) : BitConverter.ToInt32(npy, 8);
        int dataStart = (major == 1 ? 10 : 12) + headerLength;
        string header = Encoding.ASCII.GetString(npy, major == 1 ? 10 : 12, headerLength);
        if (!header.Contains("'<f4'") || header.Contains("'fortran_order': True"))
            throw new InvalidDataException("Il file delle voci Kokoro ha un formato inatteso.");
        var floats = new float[(npy.Length - dataStart) / 4];
        Buffer.BlockCopy(npy, dataStart, floats, 0, floats.Length * 4);
        return floats;
    }

    public void Dispose() => _session.Dispose();
}
