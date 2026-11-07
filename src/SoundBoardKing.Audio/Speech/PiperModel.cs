using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace SoundBoardKing.Audio.Speech;

/// <summary>
/// A Piper voice (VITS) run with ONNX Runtime, as piper.exe does: each sentence becomes phoneme ids
/// (start, a pad after every phoneme, end), is spoken on its own and followed by a short silence.
/// </summary>
internal sealed class PiperModel : IDisposable
{
    private const float SentenceSilenceSeconds = 0.2f;

    private readonly InferenceSession _session;
    private readonly Dictionary<string, long[]> _ids = [];
    private readonly int _longestPhoneme;
    private readonly float _noiseScale;
    private readonly float _lengthScale;
    private readonly float _noiseW;
    private readonly bool _hasSpeakerInput;

    public PiperModel(string modelPath, bool gpu = false)
    {
        using (var config = JsonDocument.Parse(File.ReadAllBytes(modelPath + ".json")))
        {
            var root = config.RootElement;
            SampleRate = root.GetProperty("audio").GetProperty("sample_rate").GetInt32();
            ESpeakVoice = root.TryGetProperty("espeak", out var espeak) && espeak.TryGetProperty("voice", out var voice)
                ? voice.GetString() ?? "it"
                : "it";
            var inference = root.TryGetProperty("inference", out var i) ? i : default;
            _noiseScale = Read(inference, "noise_scale", 0.667f);
            _lengthScale = Read(inference, "length_scale", 1f);
            _noiseW = Read(inference, "noise_w", 0.8f);
            foreach (var entry in root.GetProperty("phoneme_id_map").EnumerateObject())
            {
                _ids[entry.Name] = entry.Value.EnumerateArray().Select(v => v.GetInt64()).ToArray();
                _longestPhoneme = Math.Max(_longestPhoneme, entry.Name.Length);
            }
        }

        _session = OnnxSessions.Create(modelPath, gpu, out bool onGpu);
        OnGpu = onGpu;
        _hasSpeakerInput = _session.InputMetadata.ContainsKey("sid");
    }

    public int SampleRate { get; }

    /// <summary>Whether it runs on the graphics card.</summary>
    public bool OnGpu { get; }
    public string ESpeakVoice { get; }

    private static float Read(JsonElement section, string name, float fallback) =>
        section.ValueKind == JsonValueKind.Object && section.TryGetProperty(name, out var value) ? value.GetSingle() : fallback;

    /// <param name="speed">1 normal, 2 twice as fast, 0.5 half.</param>
    public float[] Synthesize(IReadOnlyList<string> sentences, float speed, CancellationToken cancellation)
    {
        var audio = new List<float>();
        int silence = (int)(SampleRate * SentenceSilenceSeconds);
        foreach (var sentence in sentences)
        {
            cancellation.ThrowIfCancellationRequested();
            long[] ids = PhonemeIds(sentence);
            if (ids.Length <= 3)
                continue;
            if (audio.Count > 0)
                audio.AddRange(new float[silence]);
            audio.AddRange(Run(ids, speed));
        }
        return [.. audio];
    }

    /// <summary>"^", then every known phoneme followed by the pad "_", then "$" — the order Piper was trained on.</summary>
    internal long[] PhonemeIds(string phonemes)
    {
        var ids = new List<long>();
        long[] pad = _ids.GetValueOrDefault("_") ?? [0];
        ids.AddRange(_ids.GetValueOrDefault("^") ?? [1]);
        ids.AddRange(pad);
        int i = 0;
        while (i < phonemes.Length)
        {
            // Newer voices also know phonemes of more than one character ("aɪ"): the longest match wins.
            int matched = 0;
            for (int length = Math.Min(_longestPhoneme, phonemes.Length - i); length > 0; length--)
            {
                if (_ids.TryGetValue(phonemes.Substring(i, length), out var id))
                {
                    ids.AddRange(id);
                    ids.AddRange(pad);
                    matched = length;
                    break;
                }
            }
            // Unknown to the voice: skipped, as Piper does.
            i += matched > 0 ? matched : char.IsSurrogatePair(phonemes, i) ? 2 : 1;
        }
        ids.AddRange(_ids.GetValueOrDefault("$") ?? [2]);
        return [.. ids];
    }

    private float[] Run(long[] ids, float speed)
    {
        float[] scales = [_noiseScale, _lengthScale / Math.Clamp(speed, 0.25f, 4f), _noiseW];
        var inputs = new List<OrtValue>
        {
            OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]),
            OrtValue.CreateTensorValueFromMemory(new long[] { ids.Length }, [1]),
            OrtValue.CreateTensorValueFromMemory(scales, [3]),
        };
        var names = new List<string> { "input", "input_lengths", "scales" };
        if (_hasSpeakerInput)
        {
            inputs.Add(OrtValue.CreateTensorValueFromMemory(new long[] { 0 }, [1]));
            names.Add("sid");
        }
        try
        {
            using var outputs = _session.Run(new RunOptions(), names, inputs, [_session.OutputNames[0]]);
            return outputs[0].GetTensorDataAsSpan<float>().ToArray();
        }
        finally
        {
            foreach (var input in inputs)
                input.Dispose();
        }
    }

    public void Dispose() => _session.Dispose();
}
