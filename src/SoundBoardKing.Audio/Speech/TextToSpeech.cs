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

    private (string Key, Rvc.RvcConverter Converter)? _rvc;

    public VoiceCatalog Catalog { get; } = new(voicesFolder);

    /// <summary>The RVC voices the user set up (their models stay where the user keeps them).</summary>
    public IReadOnlyList<RvcVoiceDefinition> RvcVoices { get; set; } = [];

    /// <summary>The Windows voices, then the neural voices downloaded, then the RVC voices; each name once.</summary>
    public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync()
    {
        var voices = new List<TtsVoice>(await SapiSpeech.GetVoicesAsync());
        var names = voices.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        voices.AddRange(Catalog.InstalledVoices().Where(v => names.Add(v.Name)));
        var bases = voices.ToList();
        foreach (var rvc in RvcVoices)
        {
            var baseVoice = bases.FirstOrDefault(v => v.Name == rvc.BaseVoice);
            if (names.Add(rvc.Name))
                voices.Add(new TtsVoice(rvc.Name, baseVoice?.Language ?? "", "RVC", TtsEngine.Rvc, rvc.Name));
        }
        return voices;
    }

    /// <summary>Whether ContentVec and RMVPE, needed by every RVC voice, are downloaded.</summary>
    public bool IsRvcReady => Catalog.IsInstalled(VoiceCatalog.RvcSupport);

    private bool _useGpu;
    private bool _gpuFailed;

    /// <summary>
    /// Run the neural voices on the graphics card (DirectML) when there is one; they fall back to the
    /// processor by themselves when it cannot. Changing it reloads the models at the next phrase.
    /// </summary>
    public bool UseGpu
    {
        get => _useGpu;
        set
        {
            if (value == _useGpu)
                return;
            _useGpu = value;
            _gpuFailed = false;
            ReleaseModels();
        }
    }

    /// <summary>Whether the last phrase was computed on the graphics card (Windows voices never are).</summary>
    public bool LastOnGpu { get; private set; }

    /// <summary>
    /// Where the last neural phrase was computed, for the user: "scheda video", "processore", or which parts
    /// stayed on the processor and why, when the graphics card was asked for. Empty for the Windows voices.
    /// </summary>
    public string LastDevice { get; private set; } = "";

    // cpuParts: the models that ran on the processor; total: how many models the phrase used.
    private void SetDevice(IReadOnlyList<string> cpuParts, int total)
    {
        LastOnGpu = _useGpu && cpuParts.Count == 0;
        if (!_useGpu)
            LastDevice = "processore";
        else if (cpuParts.Count == 0)
            LastDevice = "scheda video";
        else
        {
            string where = cpuParts.Count >= total ? "processore" : $"scheda video, ma {string.Join(", ", cpuParts)} sul processore";
            LastDevice = GpuProblem is { Length: > 0 } reason ? $"{where} (la scheda video non va: {reason})" : where;
        }
    }

    /// <summary>Why the graphics card is not used although asked, when it is not.</summary>
    public string? GpuProblem => OnnxSessions.GpuProblem;

    private bool GpuNow => _useGpu && !_gpuFailed;

    // A model that fails on the graphics card (a driver, an operator DirectML gets wrong) is loaded again on
    // the processor and the phrase is computed again, once. Runs inside the gate.
    private T WithGpuFallback<T>(Func<T> work, Func<bool> onGpu)
    {
        try
        {
            return work();
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex) when (onGpu())
        {
            OnnxSessions.Failed(ex);
            _gpuFailed = true;
            DisposeModels();
            return work();
        }
    }

    /// <summary>
    /// Speaks <paramref name="text"/> into a new wave file in <paramref name="folder"/> and returns its path.
    /// </summary>
    /// <param name="rate">-10 (slowest, half speed) .. 0 (normal) .. 10 (fastest, double speed).</param>
    /// <exception cref="InvalidOperationException">The voice could not speak.</exception>
    public async Task<string> SynthesizeAsync(string text, TtsVoice voice, int rate, string folder,
        CancellationToken cancellation = default)
    {
        if (voice.Engine == TtsEngine.Rvc)
            return await SynthesizeRvcAsync(text, voice, rate, folder, cancellation);

        Directory.CreateDirectory(folder);
        DeleteOldFiles(folder);
        string path = Path.Combine(folder, $"tts-{Guid.NewGuid():N}.wav");
        rate = Math.Clamp(rate, MinRate, MaxRate);
        try
        {
            if (voice.Engine == TtsEngine.Windows)
            {
                await SapiSpeech.SynthesizeAsync(text, voice, rate, path, cancellation);
                LastOnGpu = false;
                LastDevice = "";
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
                audio = WithGpuFallback(() =>
                {
                    _kokoro ??= new KokoroModel(Catalog.UsablePath(package, package.Files[0]), Catalog.UsablePath(package, package.Files[1]), GpuNow);
                    return _kokoro.Synthesize(sentences, speaker.Id, speed, cancellation);
                }, () => _kokoro?.OnGpu == true);
                sampleRate = KokoroModel.SampleRate;
                SetDevice(_kokoro!.OnGpu ? [] : [voice.Name], 1);
            }
            else
            {
                string model = Catalog.UsablePath(package, package.Files[0]);
                audio = WithGpuFallback(() =>
                {
                    if (_piper?.Path != model)
                    {
                        _piper?.Model.Dispose();
                        _piper = null;
                        _piper = (model, new PiperModel(model, GpuNow));
                    }
                    return _piper.Value.Model.Synthesize(sentences, speed, cancellation);
                }, () => _piper?.Model.OnGpu == true);
                sampleRate = _piper!.Value.Model.SampleRate;
                SetDevice(_piper.Value.Model.OnGpu ? [] : [voice.Name], 1);
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

    // The base voice says the text, then the RVC model changes it into its own voice.
    private async Task<string> SynthesizeRvcAsync(string text, TtsVoice voice, int rate, string folder, CancellationToken cancellation)
    {
        var definition = RvcVoices.FirstOrDefault(v => v.Name == voice.Id)
            ?? throw new InvalidOperationException($"La voce \"{voice.Name}\" non è più configurata.");
        if (!File.Exists(definition.ModelPath))
            throw new InvalidOperationException($"Il modello di \"{voice.Name}\" non c'è più: {definition.ModelPath}");
        if (!IsRvcReady)
            throw new InvalidOperationException("Mancano i componenti RVC (ContentVec e RMVPE): scaricali dalla finestra.");
        var baseVoice = (await GetVoicesAsync()).FirstOrDefault(v => v.Name == definition.BaseVoice && v.Engine != TtsEngine.Rvc)
            ?? throw new InvalidOperationException($"La voce di base \"{definition.BaseVoice}\" di \"{voice.Name}\" non è installata.");

        string basePath = await SynthesizeAsync(text, baseVoice, rate, folder, cancellation);
        string path = Path.Combine(folder, $"tts-{Guid.NewGuid():N}.wav");
        try
        {
            await _gate.WaitAsync(cancellation);
            try
            {
                await Task.Run(() =>
                {
                    float[] input = ReadMono16k(basePath);
                    // The Windows voices never use the graphics card: they do not count.
                    bool baseOnCpu = baseVoice.Engine != TtsEngine.Windows && !LastOnGpu;
                    string key = definition.ModelPath + "|" + definition.IndexPath;
                    float[] output = WithGpuFallback(() =>
                    {
                        if (_rvc?.Key != key)
                        {
                            _rvc?.Converter.Dispose();
                            _rvc = null;
                            try
                            {
                                _rvc = (key, new Rvc.RvcConverter(Catalog.ContentVecPath, Catalog.RmvpePath,
                                    definition.ModelPath, definition.IndexPath, Path.Combine(Catalog.Folder, "rvc-cache"), GpuNow));
                            }
                            catch (Exception ex) when (ex is InvalidDataException or IOException)
                            {
                                throw new InvalidOperationException($"Il modello di \"{voice.Name}\" non si carica: {ex.Message}", ex);
                            }
                        }
                        return _rvc.Value.Converter.Convert(input, new Rvc.RvcSettings(definition.Pitch, definition.IndexRate, Intonation: definition.Intonation), cancellation);
                    }, () => _rvc?.Converter.OnGpu == true);
                    var cpuParts = new List<string>();
                    if (baseOnCpu)
                        cpuParts.Add(baseVoice.Name);
                    cpuParts.AddRange(_rvc!.Value.Converter.CpuParts);
                    SetDevice(cpuParts, baseVoice.Engine == TtsEngine.Windows ? 3 : 4);
                    if (output.Length == 0)
                        throw new InvalidOperationException($"La voce \"{voice.Name}\" non ha prodotto audio: la frase è troppo corta?");
                    WriteWave(path, output, Rvc.RvcConverter.OutputRate);
                }, cancellation);
            }
            finally
            {
                _gate.Release();
            }
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        finally
        {
            TryDelete(basePath);
        }
    }

    private static float[] ReadMono16k(string path)
    {
        using var reader = new WaveFileReader(path);
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels > 1)
            source = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(source);
        if (source.WaveFormat.SampleRate != Rvc.RvcConverter.InputRate)
            source = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(source, Rvc.RvcConverter.InputRate);
        var samples = new List<float>();
        var buffer = new float[16000];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read));
        return [.. samples];
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
            DisposeModels();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Inside the gate.
    private void DisposeModels()
    {
        _piper?.Model.Dispose();
        _piper = null;
        _kokoro?.Dispose();
        _kokoro = null;
        _rvc?.Converter.Dispose();
        _rvc = null;
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
