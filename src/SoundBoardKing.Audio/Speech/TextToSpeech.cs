using NAudio.Wave;

namespace SoundBoardKing.Audio.Speech;

/// <summary>
/// Says a text with any voice, into a wave file that then plays like any other sound: the voices of
/// Windows (SAPI 5) and the neural voices downloaded from the <see cref="Catalog"/> (Piper, Kokoro),
/// which run on the PC with ONNX Runtime. The neural models stay loaded until <see cref="ReleaseModels"/>.
/// </summary>
public sealed class TextToSpeech(string voicesFolder) : IDisposable
{
    public const int MinRate = -10;
    public const int MaxRate = 10;

    // Phrases run one at a time: the models are not shared between threads, eSpeak has a global state.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ESpeak? _espeak;
    private (string Path, PiperModel Model)? _piper;
    private KokoroModel? _kokoro;

    public VoiceCatalog Catalog { get; } = new(voicesFolder);

    /// <summary>The Windows voices, then the neural voices downloaded; each name once.</summary>
    public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync()
    {
        var voices = new List<TtsVoice>(await SapiSpeech.GetVoicesAsync());
        var names = voices.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        voices.AddRange(Catalog.InstalledVoices().Where(v => names.Add(v.Name)));
        return voices;
    }

    /// <summary>
    /// Speaks <paramref name="text"/> into a new wave file in <paramref name="folder"/> and returns its path.
    /// </summary>
    /// <param name="rate">-10 (slowest, half speed) .. 0 (normal) .. 10 (fastest, double speed).</param>
    /// <exception cref="InvalidOperationException">The voice could not speak.</exception>
    public async Task<string> SynthesizeAsync(string text, TtsVoice voice, int rate, string folder,
        CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(folder);
        DeleteOldFiles(folder);
        string path = Path.Combine(folder, $"tts-{Guid.NewGuid():N}.wav");
        rate = Math.Clamp(rate, MinRate, MaxRate);
        try
        {
            if (voice.Engine == TtsEngine.Windows)
            {
                await SapiSpeech.SynthesizeAsync(text, voice, rate, path, cancellation);
            }
            else
            {
                await _gate.WaitAsync(cancellation);
                try
                {
                    await Task.Run(() => SynthesizeNeural(text, voice, rate, path, cancellation), cancellation);
                }
                finally
                {
                    _gate.Release();
                }
            }

            if (!File.Exists(path) || new FileInfo(path).Length <= 44)
                throw new InvalidOperationException($"La voce \"{voice.Name}\" non ha prodotto audio.");
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private void SynthesizeNeural(string text, TtsVoice voice, int rate, string path, CancellationToken cancellation)
    {
        if (VoiceCatalog.Find(voice.Id) is not { } found || !Catalog.IsInstalled(found.Package))
            throw new InvalidOperationException($"La voce \"{voice.Name}\" non è più installata: scaricala di nuovo.");
        var (package, speaker) = found;

        // The slider's -10..10 as a factor: half to double speed.
        float speed = MathF.Pow(2, rate / 10f);
        float[] audio;
        int sampleRate;
        try
        {
            _espeak ??= new ESpeak(Catalog.ESpeakFolder);
            var sentences = _espeak.Phonemize(text, package.ESpeakVoice);
            if (sentences.Count == 0)
                throw new InvalidOperationException("Non c'è niente da dire in questo testo.");

            if (package.Engine == TtsEngine.Kokoro)
            {
                _kokoro ??= new KokoroModel(Catalog.PathOf(package, package.Files[0]), Catalog.PathOf(package, package.Files[1]));
                audio = _kokoro.Synthesize(sentences, speaker.Id, speed, cancellation);
                sampleRate = KokoroModel.SampleRate;
            }
            else
            {
                string model = Catalog.PathOf(package, package.Files[0]);
                if (_piper?.Path != model)
                {
                    _piper?.Model.Dispose();
                    _piper = null;
                    _piper = (model, new PiperModel(model));
                }
                audio = _piper.Value.Model.Synthesize(sentences, speed, cancellation);
                sampleRate = _piper.Value.Model.SampleRate;
            }
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or DllNotFoundException
                                       or BadImageFormatException or EntryPointNotFoundException or IOException
                                       or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            throw new InvalidOperationException($"La voce \"{voice.Name}\" non funziona: {ex.Message}", ex);
        }
        WriteWave(path, audio, sampleRate);
    }

    /// <summary>Peak-normalized to -1 dBFS (as Piper does), 16-bit mono.</summary>
    private static void WriteWave(string path, float[] audio, int sampleRate)
    {
        float peak = 0;
        foreach (float sample in audio)
            peak = Math.Max(peak, Math.Abs(sample));
        float gain = peak > 0.01f ? 0.89f / peak : 1f;
        for (int i = 0; i < audio.Length; i++)
            audio[i] *= gain;
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1));
        writer.WriteSamples(audio, 0, audio.Length);
    }

    /// <summary>Frees the neural models (hundreds of MB) until the next phrase needs them.</summary>
    public void ReleaseModels()
    {
        _gate.Wait();
        try
        {
            _piper?.Model.Dispose();
            _piper = null;
            _kokoro?.Dispose();
            _kokoro = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Spoken files are only needed while they play; older ones go (a file still playing is locked and stays).
    private static void DeleteOldFiles(string folder)
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        foreach (var file in Directory.EnumerateFiles(folder, "tts-*"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
                TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        ReleaseModels();
        _espeak?.Dispose();
        _espeak = null;
    }
}
