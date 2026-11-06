using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using NAudio.Wave;

namespace SoundBoardKing.Audio.MicFx;

public enum MicFxState
{
    /// <summary>The APO has never created its shared memory: not installed, or the audio service has not loaded it.</summary>
    NotInstalled,

    /// <summary>The APO exists but no application is capturing from the microphone right now.</summary>
    Idle,

    /// <summary>An application is capturing and the APO is processing audio.</summary>
    Active,

    /// <summary>The shared memory belongs to a different APO version.</summary>
    Incompatible,

    /// <summary>The shared memory exists but this process may not open it.</summary>
    AccessDenied,
}

public sealed record MicFxStatus(MicFxState State, int SampleRate, int Channels);

/// <summary>
/// Feeds the SbkMicFx APO: writes mono samples into the shared ring at real-time pace on its
/// own thread. The APO instances read them and add them to the microphone signal.
/// </summary>
public sealed unsafe class ApoMicFeed : IMicFeed
{
    private const long ReaderTimeoutMs = 1500;
    private const long OpenRetryMs = 1000;
    private const int DefaultSampleRate = 48000;

    private readonly string _name;
    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private byte* _base;
    private long _nextOpenAttempt;
    private MicFxState _openFailure = MicFxState.NotInstalled;
    private bool _hasVoiceTap;
    private Thread? _thread;
    private volatile bool _stop;

    public ApoMicFeed() : this(MicFxLayout.SharedMemoryName) { }

    /// <summary>Tests pass a per-session name matching a test build of the APO.</summary>
    public ApoMicFeed(string sharedMemoryName) => _name = sharedMemoryName;

    public bool IsAvailable => GetStatus().State == MicFxState.Active;

    public WaveFormat? Format
    {
        get
        {
            var status = GetStatus();
            if (status.State != MicFxState.Active)
                return null;
            return WaveFormat.CreateIeeeFloatWaveFormat(status.SampleRate > 0 ? status.SampleRate : DefaultSampleRate, 1);
        }
    }

    public MicFxStatus GetStatus()
    {
        if (!TryOpen())
            return new MicFxStatus(_openFailure, 0, 0);

        long heartbeat = Volatile.Read(ref *(long*)(_base + MicFxLayout.ReaderHeartbeatOffset));
        int rate = (int)Volatile.Read(ref *(uint*)(_base + MicFxLayout.ReaderSampleRateOffset));
        int channels = (int)Volatile.Read(ref *(uint*)(_base + MicFxLayout.ReaderChannelsOffset));
        bool active = heartbeat != 0 && Environment.TickCount64 - heartbeat < ReaderTimeoutMs;
        return new MicFxStatus(active ? MicFxState.Active : MicFxState.Idle, rate, channels);
    }

    public void Attach(ISampleProvider source)
    {
        if (source.WaveFormat.Channels != 1)
            throw new ArgumentException("Il microfono riceve audio mono.", nameof(source));
        Detach();
        if (!TryOpen())
            return;

        Volatile.Write(ref *(uint*)(_base + MicFxLayout.RingSampleRateOffset), (uint)source.WaveFormat.SampleRate);
        Volatile.Write(ref *(int*)(_base + MicFxLayout.ProducerActiveOffset), 1);
        _stop = false;
        _thread = new Thread(() => Produce(source))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "SoundBoardKing mic feed",
        };
        _thread.Start();
    }

    public void Detach()
    {
        if (_thread is null)
            return;
        _stop = true;
        _thread.Join();
        _thread = null;
        if (_base != null)
            Volatile.Write(ref *(int*)(_base + MicFxLayout.ProducerActiveOffset), 0);
    }

    // Paced by a stopwatch at the source's sample rate. Readers follow the write position and
    // resynchronise on their own, so small differences against the microphone clock are harmless.
    private void Produce(ISampleProvider source)
    {
        int rate = source.WaveFormat.SampleRate;
        var buffer = new float[rate / 5];
        var ring = (float*)(_base + MicFxLayout.HeaderSize);
        const long mask = MicFxLayout.RingFrames - 1;
        long writePos = Volatile.Read(ref *(long*)(_base + MicFxLayout.WritePosOffset));
        var clock = Stopwatch.StartNew();
        long produced = 0;

        while (!_stop)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * rate);
            long pending = due - produced;
            if (pending > buffer.Length)
            {
                // The thread was starved; skip the backlog rather than fall permanently behind.
                produced = due - buffer.Length;
                pending = buffer.Length;
            }

            if (pending > 0)
            {
                int count = (int)pending;
                int read = source.Read(buffer, 0, count);
                if (read < count)
                    Array.Clear(buffer, read, count - read);
                for (int i = 0; i < count; i++)
                    ring[(writePos + i) & mask] = buffer[i];

                writePos += count;
                produced += count;
                Volatile.Write(ref *(long*)(_base + MicFxLayout.WritePosOffset), writePos);
                Volatile.Write(ref *(long*)(_base + MicFxLayout.ProducerHeartbeatOffset), Environment.TickCount64);
            }
            Thread.Sleep(5);
        }
    }

    private bool TryOpen()
    {
        if (_base != null)
            return true;
        long now = Environment.TickCount64;
        if (now < _nextOpenAttempt)
            return false;
        _nextOpenAttempt = now + OpenRetryMs;

        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            file = MemoryMappedFile.OpenExisting(_name, MemoryMappedFileRights.ReadWrite);
            // An older APO made it without the voice tap: the microphone works all the same.
            try
            {
                view = file.CreateViewAccessor(0, MicFxLayout.Size, MemoryMappedFileAccess.ReadWrite);
                _hasVoiceTap = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                view = file.CreateViewAccessor(0, MicFxLayout.LegacySize, MemoryMappedFileAccess.ReadWrite);
                _hasVoiceTap = false;
            }
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointer += view.PointerOffset;

            uint magic = Volatile.Read(ref *(uint*)(pointer + MicFxLayout.MagicOffset));
            if (magic != MicFxLayout.Magic)
            {
                // Just created by the APO and not stamped yet: try again on the next poll.
                _openFailure = MicFxState.NotInstalled;
                return Release();
            }
            if (*(uint*)(pointer + MicFxLayout.VersionOffset) != MicFxLayout.Version ||
                *(uint*)(pointer + MicFxLayout.CapacityOffset) != MicFxLayout.RingFrames)
            {
                _openFailure = MicFxState.Incompatible;
                return Release();
            }

            _file = file;
            _view = view;
            _base = pointer;
            return true;
        }
        catch (FileNotFoundException)
        {
            _openFailure = MicFxState.NotInstalled;
            return Release();
        }
        catch (UnauthorizedAccessException)
        {
            _openFailure = MicFxState.AccessDenied;
            return Release();
        }

        bool Release()
        {
            if (view is not null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
            }
            file?.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Effect the APO applies to the voice itself (0 = none; same numbers as SoundEffect). Call it
    /// about once a second: the APO drops the effect when it stops hearing from the app.
    /// </summary>
    public void SetVoiceEffect(int effect, float amount)
    {
        if (!TryOpen())
            return;
        Volatile.Write(ref *(int*)(_base + MicFxLayout.VoiceEffectOffset), effect);
        Volatile.Write(ref *(float*)(_base + MicFxLayout.VoiceAmountOffset), Math.Clamp(amount, 0f, 1f));
        // 0 means "never": skip it when the tick count wraps onto it.
        uint beat = (uint)Environment.TickCount64;
        Volatile.Write(ref *(uint*)(_base + MicFxLayout.VoiceHeartbeatOffset), effect == 0 ? 0 : Math.Max(beat, 1));
    }

    /// <summary>
    /// Copies the latest <paramref name="window"/>.Length samples of the voice (as the others hear it,
    /// before the sounds) when new ones arrived since <paramref name="seen"/>. False when the APO has
    /// not written lately: nobody is recording, or the APO is too old to tap the voice.
    /// </summary>
    public bool TryReadVoice(float[] window, ref long seen, out int sampleRate)
    {
        sampleRate = 0;
        if (!TryOpen() || !_hasVoiceTap || window.Length > MicFxLayout.VoiceTapFrames)
            return false;
        byte* tap = _base + MicFxLayout.VoiceTapOffset;
        long beat = Volatile.Read(ref *(long*)(tap + MicFxLayout.VoiceTapHeartbeatOffset));
        long written = Volatile.Read(ref *(long*)(tap + MicFxLayout.VoiceTapWritePosOffset));
        if (beat == 0 || Environment.TickCount64 - beat > 250 || written == seen || written < window.Length)
            return false;
        seen = written;
        sampleRate = (int)Volatile.Read(ref *(uint*)(tap + MicFxLayout.VoiceTapSampleRateOffset));

        var ring = (float*)(tap + MicFxLayout.VoiceTapHeaderSize);
        const long mask = MicFxLayout.VoiceTapFrames - 1;
        long from = written - window.Length;
        for (int i = 0; i < window.Length; i++)
            window[i] = ring[(from + i) & mask];
        return sampleRate > 0;
    }

    public void Dispose()
    {
        Detach();
        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
        _file?.Dispose();
        _view = null;
        _file = null;
        _base = null;
    }
}
