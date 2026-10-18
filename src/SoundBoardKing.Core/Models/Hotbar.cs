namespace SoundBoardKing.Core.Models;

/// <summary>The favourites bar: <see cref="PageCount"/> pages of <see cref="SlotsPerPage"/> buttons.</summary>
public sealed class Hotbar
{
    public const int PageCount = 5;
    public const int SlotsPerPage = 100;

    /// <summary>Keyed by page * 1000 + slot, the same numbering SoundPad uses.</summary>
    public Dictionary<int, Guid> Slots { get; set; } = [];

    public static bool IsValid(int page, int slot) =>
        page is >= 0 and < PageCount && slot is >= 0 and < SlotsPerPage;

    public static int Key(int page, int slot)
    {
        if (!IsValid(page, slot))
            throw new ArgumentOutOfRangeException(nameof(slot), $"Slot {page}/{slot} fuori intervallo.");
        return page * 1000 + slot;
    }

    public Guid? Get(int page, int slot) =>
        Slots.TryGetValue(Key(page, slot), out var id) ? id : null;

    public void Set(int page, int slot, Guid? soundId)
    {
        var key = Key(page, slot);
        if (soundId is Guid id)
            Slots[key] = id;
        else
            Slots.Remove(key);
    }

    public void RemoveSounds(IReadOnlySet<Guid> soundIds)
    {
        foreach (var key in Slots.Where(p => soundIds.Contains(p.Value)).Select(p => p.Key).ToList())
            Slots.Remove(key);
    }
}
