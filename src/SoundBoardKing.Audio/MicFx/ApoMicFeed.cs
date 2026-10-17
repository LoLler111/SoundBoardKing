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
            view = file.CreateViewAccessor(0, MicFxLayout.Size, MemoryMappedFileAccess.ReadWrite);
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
