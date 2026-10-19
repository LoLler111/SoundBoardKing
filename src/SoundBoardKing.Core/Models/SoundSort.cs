using System.Text.Json.Serialization;

namespace SoundBoardKing.Core.Models;

/// <summary>How a list of sounds (all sounds, or one category) is kept in order.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SoundSort>))]
public enum SoundSort
{
    /// <summary>The order the user arranged by dragging.</summary>
    Manual,
    Name,
    Newest,
    MostPlayed,
    RecentlyPlayed,
    Shortest,
    Longest,
}

public static class SoundSorter
{
    /// <summary>
    /// Puts <paramref name="ids"/> in <paramref name="sort"/> order; Manual leaves them as they are.
    /// Ties keep their current order, so sorting twice changes nothing.
    /// </summary>
    public static void Apply(List<Guid> ids, SoundSort sort, Func<Guid, Sound?> find)
    {
        if (sort == SoundSort.Manual || ids.Count < 2)
            return;
        var position = new Dictionary<Guid, int>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
            position.TryAdd(ids[i], i);
        var sorted = ids
            .Select(id => (id, sound: find(id)))
            .OrderBy(x => x, new Comparer(sort, position))
            .Select(x => x.id)
            .ToList();
        ids.Clear();
        ids.AddRange(sorted);
    }

    /// <summary>Same as <see cref="Apply(List{Guid}, SoundSort, Func{Guid, Sound?})"/> for the library's own list.</summary>
    public static void Apply(List<Sound> sounds, SoundSort sort)
    {
        if (sort == SoundSort.Manual || sounds.Count < 2)
            return;
        var byId = sounds.ToDictionary(s => s.Id);
        var ids = sounds.Select(s => s.Id).ToList();
        Apply(ids, sort, byId.GetValueOrDefault);
        sounds.Clear();
        sounds.AddRange(ids.Select(id => byId[id]));
    }

    /// <summary>Like Explorer: numbers count as numbers, so "2 urla" comes before "10 urla"; case does not matter.</summary>
    public static int CompareNatural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var numberA = a.AsSpan(startA, i - startA).TrimStart('0');
                var numberB = b.AsSpan(startB, j - startB).TrimStart('0');
                int result = numberA.Length != numberB.Length
                    ? numberA.Length.CompareTo(numberB.Length)
                    : numberA.SequenceCompareTo(numberB);
                if (result != 0)
                    return result;
            }
            else
            {
                int startA = i, startB = j;
                while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;
                int result = System.Globalization.CultureInfo.CurrentCulture.CompareInfo.Compare(
                    a.AsSpan(startA, i - startA), b.AsSpan(startB, j - startB), System.Globalization.CompareOptions.IgnoreCase);
                if (result != 0)
                    return result;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private sealed class Comparer(SoundSort sort, Dictionary<Guid, int> position) : IComparer<(Guid id, Sound? sound)>
    {
        public int Compare((Guid id, Sound? sound) a, (Guid id, Sound? sound) b)
        {
            // Unknown ids (a sound removed elsewhere) sink to the bottom.
            if (a.sound is null || b.sound is null)
                return a.sound is null == b.sound is null ? Tie(a, b) : a.sound is null ? 1 : -1;
            var x = a.sound;
            var y = b.sound;
            int result = sort switch
            {
                SoundSort.Name => CompareNatural(x.Title, y.Title),
                // Same day: the one added later sits later in the list, so it comes first.
                SoundSort.Newest => y.AddedOn.CompareTo(x.AddedOn) is var day and not 0 ? day : -Tie(a, b),
                SoundSort.MostPlayed => y.PlayCount.CompareTo(x.PlayCount),
                SoundSort.RecentlyPlayed => Nullable.Compare(y.LastPlayed, x.LastPlayed),
                SoundSort.Shortest => x.DurationMs.CompareTo(y.DurationMs),
                SoundSort.Longest => y.DurationMs.CompareTo(x.DurationMs),
                _ => 0,
            };
            return result != 0 ? result : Tie(a, b);
        }

        private int Tie((Guid id, Sound? sound) a, (Guid id, Sound? sound) b) =>
            position.GetValueOrDefault(a.id).CompareTo(position.GetValueOrDefault(b.id));
    }
}
