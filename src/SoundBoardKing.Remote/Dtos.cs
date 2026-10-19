namespace SoundBoardKing.Remote;

// Wire format of docs/PROTOCOLLO_REMOTO.md (serialized camelCase).

public sealed record SoundDto(Guid Id, string Title, string Artist, long DurationMs, bool Missing, string? Hotkey);

public sealed record CategoryDto(Guid Id, string Name, IReadOnlyList<Guid> SoundIds, IReadOnlyList<CategoryDto> Children);

public sealed record HotbarSlotDto(int Page, int Slot, Guid SoundId);

public sealed record HotbarDto(int Pages, int SlotsPerPage, IReadOnlyList<HotbarSlotDto> Slots);

/// <summary>One of the sounds playing at the same time; <see cref="Id"/> stops just this one.</summary>
public sealed record PlayingDto(Guid Id, Guid? SoundId, long PositionMs, long DurationMs, bool Paused = false);

/// <summary>
/// <see cref="State"/> is "stopped", "playing" or "paused" (all sounds pause together). SoundId and the
/// times describe the most recent sound, for older phones; <see cref="Playing"/> lists every sound, oldest first.
/// </summary>
public sealed record PlaybackDto(string State, Guid? SoundId, long PositionMs, long DurationMs, IReadOnlyList<PlayingDto> Playing)
{
    public static PlaybackDto Stopped { get; } = new("stopped", null, 0, 0, []);
}

/// <summary><see cref="State"/> is "notInstalled", "idle", "active", "incompatible" or "accessDenied".</summary>
public sealed record MicDto(string State, bool Enabled);

public sealed record StateDto(
    string ServerId,
    string Name,
    int Protocol,
    long LibraryVersion,
    IReadOnlyList<SoundDto> Sounds,
    IReadOnlyList<CategoryDto> Categories,
    HotbarDto Hotbar,
    PlaybackDto Playback,
    MicDto Mic,
    string Effect = "none",
    int EffectAmount = 70,
    double PlaybackSpeed = 1.0,
    string EffectTarget = "sounds");

/// <summary>Outcome of a play request: the sound may not exist, or exist but fail to play (missing file, no audio device).</summary>
public sealed record PlayResult(bool Found, string? Error)
{
    public static PlayResult Started { get; } = new(true, null);
    public static PlayResult NotFound { get; } = new(false, "Suono non trovato.");
    public static PlayResult Failed(string error) => new(true, error);
}

internal sealed record PairRequest(string? Code, string? DeviceName);
