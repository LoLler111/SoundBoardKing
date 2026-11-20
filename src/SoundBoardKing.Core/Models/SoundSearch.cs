using System.Globalization;
using System.Text;

namespace SoundBoardKing.Core.Models;

/// <summary>
/// The search of the sounds list. Only letters and digits count, without accents or case:
/// "peter griffin" finds "peter-griffin", "Peter_Griffin" and "PeterGriffin". Every word typed has
/// to be in the title or in the artist, in any order.
/// </summary>
public sealed class SoundSearch
{
    private readonly string[] _words;

    public SoundSearch(string query)
    {
        _words = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Compact)
            .Where(w => w.Length > 0)
            .ToArray();
    }

    /// <summary>Nothing to look for: only blanks or symbols were typed.</summary>
    public bool IsEmpty => _words.Length == 0;

    public bool Matches(string title, string artist)
    {
        if (IsEmpty)
            return true;
        var t = Compact(title);
        var a = Compact(artist);
        return _words.All(w => t.Contains(w, StringComparison.Ordinal) || a.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Letters and digits only, lower case, without accents: "Perché-NO_2" → "percheno2".</summary>
    public static string Compact(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
