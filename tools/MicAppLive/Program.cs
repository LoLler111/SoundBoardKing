// Usage: MicAppLive [microphone name part] [sound file] [output folder]
// End-to-end check of "play into the microphone" with the app's own engine: records a capture
// device the way other programs do (default, communications and raw streams) while
// AudioEngine plays a real sound file with Mic only, then reports whether the sound got in.
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundBoardKing.Audio;
using SoundBoardKing.Audio.MicFx;

var micName = args.Length > 0 ? args[0] : "CABLE Output";
var soundPath = args.Length > 1 ? args[1] : WriteTone(Path.Combine(Path.GetTempPath(), "micapplive-tone.wav"));
var outDir = args.Length > 2 ? args[2] : ".";

using var enumerator = new MMDeviceEnumerator();
foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
    Console.WriteLine($"  microfono disponibile: {d.FriendlyName}");
var device = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
    .FirstOrDefault(d => d.FriendlyName.Contains(micName, StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidOperationException($"Microfono \"{micName}\" non trovato.");
Console.WriteLine($"microfono: {device.FriendlyName}");
Console.WriteLine($"suono: {soundPath} (esiste: {File.Exists(soundPath)})");

var results = new List<string>();
foreach (var mode in new[] { "default", "communications", "raw" })
{
    Console.WriteLine();
    Console.WriteLine($"=== modalità {mode}");
    try
    {
        results.Add($"{mode,-15} {RunMode(device, mode)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERRORE: {ex.GetType().Name}: {ex.Message}");
        results.Add($"{mode,-15} errore: {ex.Message}");
    }
    Thread.Sleep(1500);
}

Console.WriteLine();
Console.WriteLine("=== RIEPILOGO");
results.ForEach(Console.WriteLine);

// A 4 s, 440 Hz tone, used when no sound file is given.
static string WriteTone(string path)
{
    const int rate = 48000;
    using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
    var samples = new float[rate * 4];
    for (int i = 0; i < samples.Length; i++)
        samples[i] = 0.4f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
    writer.WriteSamples(samples, 0, samples.Length);
    return path;
}

string RunMode(MMDevice mic, string mode)
{
    using var recorder = new Recorder(mic, mode);
    recorder.Start();

    // The APO reports in only while something captures from the microphone.
    using var feed = new ApoMicFeed();
    var deadline = DateTime.UtcNow.AddSeconds(6);
    while (!feed.IsAvailable && DateTime.UtcNow < deadline)
        Thread.Sleep(100);
    var status = feed.GetStatus();
    Console.WriteLine($"stato modulo: {status.State}, {status.SampleRate} Hz, {status.Channels} canali");
    if (status.State != MicFxState.Active)
    {
        Thread.Sleep(1000);
        recorder.Stop();
        Report(recorder, double.NaN, double.NaN, mode);
        return $"modulo non attivo ({status.State}): il suono non può arrivare";
    }

    Thread.Sleep(800);
    using var engine = new AudioEngine(feed);
    double start = recorder.Seconds;
    var playback = engine.Play(soundPath, new PlayRequest(null, Speakers: false, Mic: true));
    Console.WriteLine($"riproduzione partita (durata {playback.Duration.TotalSeconds:F1}s), solo microfono");
    Thread.Sleep(3000);
    engine.StopAll();
    double end = recorder.Seconds;
    Thread.Sleep(800);
    recorder.Stop();
    var (before, during) = Report(recorder, start, end, mode);
    bool heard = during > 0.01 && during > before * 5;
    return $"prima {before:F4}  durante {during:F4}  → {(heard ? "SUONO ARRIVATO" : "NIENTE")}";
}

(double before, double during) Report(Recorder recorder, double start, double end, string mode)
{
    var data = recorder.Samples;
    int rate = recorder.SampleRate;
    int window = rate / 4;
    double beforeSum = 0, duringSum = 0;
    int beforeN = 0, duringN = 0;
    for (int s = 0; s + window <= data.Length; s += window)
    {
        double sum = 0;
        for (int i = s; i < s + window; i++) sum += data[i] * data[i];
        double rms = Math.Sqrt(sum / window);
        double t = s / (double)rate;
        Console.WriteLine($"{t,5:F2}s  RMS {rms:F4}  {new string('#', (int)Math.Min(60, rms * 150))}");
        if (!double.IsNaN(start) && t + 0.25 <= start) { beforeSum += rms; beforeN++; }
        if (!double.IsNaN(start) && t >= start + 0.3 && t + 0.25 <= end) { duringSum += rms; duringN++; }
    }
    var wav = Path.Combine(outDir, $"micapp-{mode}.wav");
    using (var writer = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)))
        writer.WriteSamples(data, 0, data.Length);
    Console.WriteLine($"WAV: {wav}");
    return (beforeN > 0 ? beforeSum / beforeN : 0, duringN > 0 ? duringSum / duringN : 0);
}

/// <summary>Shared-mode capture with a chosen stream category/option, first channel only.</summary>
sealed class Recorder : IDisposable
{
    private readonly AudioClient _client;
    private readonly WaveFormat _format;
    private readonly List<float> _samples = [];
    private Thread? _thread;
    private volatile bool _stop;

    public Recorder(MMDevice device, string mode)
    {
        _client = device.AudioClient;
        if (mode != "default")
        {
            var props = new AudioClientProperties
            {
                cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                bIsOffload = 0,
                eCategory = mode == "communications" ? AudioStreamCategory.Communications : AudioStreamCategory.Other,
                Options = mode == "raw" ? AudioClientStreamOptions.Raw : AudioClientStreamOptions.None,
            };
            int hr = ((IAudioClient2Props)typeof(AudioClient).GetField("audioClientInterface", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_client)!).SetClientProperties(ref props);
            Marshal.ThrowExceptionForHR(hr);
        }
        _format = _client.MixFormat;
        _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 2_000_000, 0, _format, Guid.Empty);
        Console.WriteLine($"formato: {_format.Encoding} {_format.SampleRate} Hz {_format.Channels} canali {_format.BitsPerSample} bit");
    }

    public int SampleRate => _format.SampleRate;

    public double Seconds
    {
        get { lock (_samples) return _samples.Count / (double)_format.SampleRate; }
    }

    public float[] Samples
    {
        get { lock (_samples) return [.. _samples]; }
    }

    public void Start()
    {
        _client.Start();
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join();
        _client.Stop();
    }

    private unsafe void Loop()
    {
        var capture = _client.AudioCaptureClient;
        bool isFloat = _format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (_format.Encoding == WaveFormatEncoding.Extensible && _format.BitsPerSample == 32);
        while (!_stop)
        {
            Thread.Sleep(10);
            while (capture.GetNextPacketSize() > 0)
            {
                var data = capture.GetBuffer(out int frames, out var flags);
                lock (_samples)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        float value = (flags & AudioClientBufferFlags.Silent) != 0 ? 0f
                            : isFloat ? ((float*)data)[f * _format.Channels]
                            : ((short*)data)[f * _format.Channels] / 32768f;
                        _samples.Add(value);
                    }
                }
                capture.ReleaseBuffer(frames);
            }
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>IAudioClient2: only SetClientProperties is used; the first slots keep the vtable order.</summary>
[ComImport, Guid("726778CD-F60A-4eda-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient2Props
{
    void Initialize(); void GetBufferSize(); void GetStreamLatency(); void GetCurrentPadding();
    void IsFormatSupported(); void GetMixFormat(); void GetDevicePeriod(); void Start();
    void Stop(); void Reset(); void SetEventHandle(); void GetService();
    [PreserveSig] int IsOffloadCapable(int category, out int capable);
    [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
}
