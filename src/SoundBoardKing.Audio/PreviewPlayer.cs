using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundBoardKing.Audio;

/// <summary>
/// Listens to a sound that is not in the library yet, straight from memory (nothing is written to
/// disk), only in the headphones and with its own volume. It is levelled like the library sounds
/// and limited, so an ear-rape upload is not worse than any other sound. The sound editor plays the
/// sound being edited through it too, as it is. One at a time; methods
/// are meant for the UI thread, <see cref="Ended"/> is raised on it.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
    private const int OutputLatencyMs = 80;

    private readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    private readonly LevelingSwitch _limiterOn = new() { Enabled = true };
    private WasapiOut? _output;
    private IDisposable? _resource;
    private VolumeSampleProvider? _volumeStage;
    private FrameCounter? _counter;
    private float _volume = 0.5f;

    /// <summary>The current preview finished or was stopped; the argument is its tag.</summary>
    public event EventHandler<object?>? Ended;

    public object? CurrentTag { get; private set; }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_volumeStage is not null)
                _volumeStage.Volume = _volume;
        }
    }

    /// <summary>Loudness of an mp3 held in memory, to level it; null when it cannot be decoded.</summary>
    public static LoudnessInfo? TryMeasure(byte[] data)
    {
        try
        {
            using var reader = OpenReader(data);
            return Loudness.Measure(reader.ToSampleProvider());
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Stops whatever is previewing and plays <paramref name="data"/> on the device (null = Windows default).</summary>
    /// <exception cref="COMException">The data cannot be decoded or the device cannot open.</exception>
    public void Play(byte[] data, object? tag, string? deviceId, float levelGain)
    {
        Stop();
        var reader = OpenReader(data);
        Start(reader.ToSampleProvider(), reader, tag, deviceId, levelGain);
    }

    /// <summary>
    /// Plays a sound being edited as it is, without leveling; <see cref="Position"/> follows it.
    /// </summary>
    /// <exception cref="COMException">The device cannot open.</exception>
    public void Play(ISampleProvider source, object? tag, string? deviceId)
    {
        Stop();
        Start(source, null, tag, deviceId, 1f);
    }

    /// <summary>How far the current preview has got, as heard (the output's latency taken off).</summary>
    public TimeSpan Position
    {
        get
        {
            if (_counter is not { } counter)
                return TimeSpan.Zero;
            var heard = TimeSpan.FromSeconds((double)counter.Frames / counter.WaveFormat.SampleRate) - TimeSpan.FromMilliseconds(OutputLatencyMs);
            return heard > TimeSpan.Zero ? heard : TimeSpan.Zero;
        }
    }

    private void Start(ISampleProvider input, IDisposable? resource, object? tag, string? deviceId, float levelGain)
    {
        WasapiOut? output = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = FindDevice(enumerator, deviceId) ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mixFormat = device.AudioClient.MixFormat;

            var counter = new FrameCounter(input);
            var source = SampleChains.ToFormat(counter,
                WaveFormat.CreateIeeeFloatWaveFormat(mixFormat.SampleRate, 2));
            var levelled = new VolumeSampleProvider(source) { Volume = levelGain };
            var volume = new VolumeSampleProvider(new Limiter(levelled, _limiterOn)) { Volume = _volume };
            ISampleProvider final = mixFormat.Channels == 2 ? volume : new ChannelMapSampleProvider(volume, mixFormat.Channels);

            output = new WasapiOut(device, AudioClientShareMode.Shared, true, OutputLatencyMs);
            output.PlaybackStopped += OnStopped;
            output.Init(new SampleToWaveProvider(final));
            output.Play();

            _resource = resource;
            _output = output;
            _volumeStage = volume;
            _counter = counter;
            CurrentTag = tag;
        }
        catch
        {
            output?.Dispose();
            resource?.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        if (_output is null)
            return;
        var tag = CurrentTag;
        Close();
        Raise(tag);
    }

    private static StreamMediaFoundationReader OpenReader(byte[] data) =>
        new(new MemoryStream(data, writable: false), new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true,
        });

    private static MMDevice? FindDevice(MMDeviceEnumerator enumerator, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        try
        {
            var device = enumerator.GetDevice(id);
            return device.State == DeviceState.Active ? device : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    // Raised on the UI thread (WasapiOut posts it there) when the sound ends by itself.
    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _output))
            return;
        var tag = CurrentTag;
        Close();
        Raise(tag);
    }

    private void Close()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnStopped;
            _output.Dispose();
        }
        _resource?.Dispose();
        _output = null;
        _resource = null;
        _volumeStage = null;
        _counter = null;
        CurrentTag = null;
    }

    private void Raise(object? tag)
    {
        if (Ended is not { } handler)
            return;
        if (_sync is null || _sync == SynchronizationContext.Current)
            handler(this, tag);
        else
            _sync.Post(_ => handler(this, tag), null);
    }

    public void Dispose()
    {
        Ended = null;
        Close();
    }

    // Counts the frames taken from the source, which the output thread reads ahead of what is heard.
    private sealed class FrameCounter(ISampleProvider source) : ISampleProvider
    {
        private long _frames;

        public WaveFormat WaveFormat => source.WaveFormat;
        public long Frames => Interlocked.Read(ref _frames);

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            Interlocked.Add(ref _frames, read / WaveFormat.Channels);
            return read;
        }
    }
}
