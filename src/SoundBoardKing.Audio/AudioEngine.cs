using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundBoardKing.Audio;

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Plays sounds on the selected output device and, when a mic feed is available, into the microphone.
/// Each destination has its own mixer and decodes its own copy of the file, so the two sides are
/// paced by their own clocks. Public members are meant to be called from the UI thread; events are
/// raised on the thread that created the engine.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int OutputLatencyMs = 60;

    private readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    private readonly IMicFeed _micFeed;
    private readonly object _gate = new();
    private readonly List<Playback> _playing = [];

    private WasapiOut? _output;
    private MixingSampleProvider? _speakerMixer;
    private VolumeSampleProvider? _speakerMaster;
    private MixingSampleProvider? _micMixer;
    private VolumeSampleProvider? _micMaster;
    private SpectrumTap? _speakerTap;
    private SpectrumTap? _micTap;
    private string? _deviceId;
    private float _speakerVolume = 1f;
    private float _micVolume = 1f;
    private bool _paused;

    public AudioEngine(IMicFeed? micFeed = null) => _micFeed = micFeed ?? new NullMicFeed();

    public event EventHandler<Playback>? PlaybackStarted;
    public event EventHandler<Playback>? PlaybackEnded;
    public event EventHandler<Exception>? OutputFailed;

    public bool IsMicAvailable => _micFeed.IsAvailable;
    public bool IsPaused => _paused;

    /// <summary>The most recently started sound that is still playing.</summary>
    public Playback? Current
    {
        get { lock (_gate) return _playing.Count > 0 ? _playing[^1] : null; }
    }

    public float SpeakerVolume
    {
        get => _speakerVolume;
        set
        {
            _speakerVolume = Math.Clamp(value, 0f, 1f);
            if (_speakerMaster is not null)
                _speakerMaster.Volume = _speakerVolume;
        }
    }

    public float MicVolume
    {
        get => _micVolume;
        set
        {
            _micVolume = Math.Clamp(value, 0f, 1f);
            if (_micMaster is not null)
                _micMaster.Volume = _micVolume;
        }
    }

    public static IReadOnlyList<AudioDevice> GetOutputDevices() => GetDevices(DataFlow.Render);

    /// <summary>Microphones, for installing the mic module on one of them.</summary>
    public static IReadOnlyList<AudioDevice> GetCaptureDevices() => GetDevices(DataFlow.Capture);

    private static IReadOnlyList<AudioDevice> GetDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
                devices.Add(new AudioDevice(device.ID, device.FriendlyName));
        }
        return devices;
    }

    /// <summary>Spectrum of what goes to the headphones/speakers, 0..1 per band.</summary>
    public void ReadSpeakerSpectrum(float[] levels) => ReadSpectrum(_speakerTap, levels);

    /// <summary>Spectrum of what goes into the microphone only.</summary>
    public void ReadMicSpectrum(float[] levels) => ReadSpectrum(_micTap, levels);

    private static void ReadSpectrum(SpectrumTap? tap, float[] levels)
    {
        if (tap is null)
            Array.Clear(levels);
        else
            tap.Read(levels);
    }

    public bool IsPlayingTag(object tag)
    {
        lock (_gate) return _playing.Exists(p => Equals(p.Tag, tag));
    }

    /// <summary>Switches the speaker output; null follows the Windows default device.</summary>
    public void SetOutputDevice(string? deviceId)
    {
        _deviceId = deviceId;
        CloseOutput();
    }

    /// <exception cref="COMException">The file cannot be decoded or no output device exists.</exception>
    public Playback Play(string path, PlayRequest request)
    {
        var playback = new Playback(request.Tag);
        Voice? speaker = null;
        Voice? mic = null;
        try
        {
            if (request.Speakers)
            {
                EnsureOutput();
                speaker = new Voice(path, _speakerMixer!.WaveFormat, request.Volume, playback);
            }
            if (request.Mic && EnsureMic())
                mic = new Voice(path, _micMixer!.WaveFormat, request.Volume, playback);
        }
        catch
        {
            speaker?.Dispose();
            mic?.Dispose();
            throw;
        }

        if (speaker is null && mic is null)
            throw new InvalidOperationException("Nessuna uscita attiva: abilita le cuffie o il microfono.");

        if (!request.AllowOverlap)
            StopAll();
        Resume();

        playback.Attach(speaker, mic);
        lock (_gate)
            _playing.Add(playback);
        if (speaker is not null)
            _speakerMixer!.AddMixerInput(speaker);
        if (mic is not null)
            _micMixer!.AddMixerInput(mic);

        Post(PlaybackStarted, playback);
        return playback;
    }

    /// <summary>Stops one sound; the others keep playing.</summary>
    public void Stop(Playback playback) => playback.Stop();

    public void StopAll()
    {
        foreach (var playback in Snapshot())
            playback.Stop();
        _paused = false;
    }

    public void TogglePause()
    {
        var playing = Snapshot();
        _paused = playing.Count > 0 && !_paused;
        foreach (var playback in playing)
            playback.SetPaused(_paused);
    }

    private void Resume()
    {
        if (!_paused)
            return;
        _paused = false;
        foreach (var playback in Snapshot())
            playback.SetPaused(false);
    }

    private List<Playback> Snapshot()
    {
        lock (_gate) return [.. _playing];
    }

    private void EnsureOutput()
    {
        if (_output is not null)
            return;

        using var enumerator = new MMDeviceEnumerator();
        var device = FindDevice(enumerator, _deviceId)
            ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        // Mix at the device's own rate so Windows does not have to resample; voices are converted to it.
        var mixFormat = device.AudioClient.MixFormat;
        _speakerMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(mixFormat.SampleRate, 2))
        {
            ReadFully = true,
        };
        _speakerMixer.MixerInputEnded += OnMixerInputEnded;
        _speakerMaster = new VolumeSampleProvider(_speakerMixer) { Volume = _speakerVolume };
        _speakerTap = new SpectrumTap(_speakerMaster);
        ISampleProvider final = mixFormat.Channels == 2
            ? _speakerTap
            : new ChannelMapSampleProvider(_speakerTap, mixFormat.Channels);

        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, OutputLatencyMs);
        output.PlaybackStopped += OnOutputStopped;
        output.Init(new SampleToWaveProvider(final));
        output.Play();
        _output = output;
    }

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

    private bool EnsureMic()
    {
        if (_micMixer is not null)
            return true;
        if (!_micFeed.IsAvailable || _micFeed.Format is not { } format)
            return false;

        _micMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels))
        {
            ReadFully = true,
        };
        _micMixer.MixerInputEnded += OnMixerInputEnded;
        _micMaster = new VolumeSampleProvider(_micMixer) { Volume = _micVolume };
        _micTap = new SpectrumTap(_micMaster);
        _micFeed.Attach(_micTap);
        return true;
    }

    // Runs on the audio thread, inside the mixer's lock: never call back into a mixer from here.
    private void OnMixerInputEnded(object? sender, SampleProviderEventArgs e)
    {
        if (e.SampleProvider is Voice voice)
            EndVoice(voice);
    }

    private void EndVoice(Voice voice)
    {
        if (!voice.TryMarkEnded())
            return;
        voice.Dispose();

        var playback = voice.Owner;
        bool ended;
        lock (_gate)
        {
            ended = playback.OnVoiceEnded();
            if (ended)
                _playing.Remove(playback);
        }
        if (ended)
            Post(PlaybackEnded, playback);
    }

    private void OnOutputStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _output))
            return;
        CloseOutput();
        if (e.Exception is not null)
            Post(OutputFailed, e.Exception);
    }

    private void CloseOutput()
    {
        if (_output is null)
            return;

        _output.PlaybackStopped -= OnOutputStopped;
        _output.Dispose();
        _output = null;
        _speakerMixer = null;
        _speakerMaster = null;
        _speakerTap = null;

        // The output thread has stopped, so nothing reads the speaker voices any more.
        foreach (var playback in Snapshot())
        {
            playback.Stop();
            if (playback.SpeakerVoice is { } voice)
                EndVoice(voice);
        }
    }

    private void Post<T>(EventHandler<T>? handler, T argument)
    {
        if (handler is null)
            return;
        if (_sync is null)
            handler(this, argument);
        else
            _sync.Post(_ => handler(this, argument), null);
    }

    public void Dispose()
    {
        CloseOutput();
        _micFeed.Detach();
        _micFeed.Dispose();
        foreach (var playback in Snapshot())
        {
            foreach (var voice in playback.Voices)
                EndVoice(voice);
        }
    }
}
