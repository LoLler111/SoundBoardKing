using System.Text.RegularExpressions;

namespace SoundBoardKing.Core.Import;

/// <summary>Turns a file name like "01_bruh_sound_effect (1)" into a readable title: "Bruh sound effect".</summary>
public static partial class TitleCleaner
{
    public static string Clean(string name)
    {
        var title = name.Replace("%20", " ").Replace('_', ' ');
        title = Spaces().Replace(title, " ").Trim();

        // Copies and duplicates: "x (1)", "x - Copia", "x - Copy (2)", repeated.
        string previous;
        do
        {
            previous = title;
            title = CopySuffix().Replace(title, "").Trim();
        }
        while (title != previous);

        // A track number in front: "01 x", "3 - x", "12. x"; a plain number that is part of the name ("2 fast") stays.
        title = TrackNumber().Replace(title, "").Trim();
        title = title.Trim('-', '.', ' ');

        if (title.Length == 0)
            return name.Trim();
        return char.ToUpper(title[0]) + title[1..];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(\s*\(\d{1,3}\)|\s*[-–]\s*(copia|copy)(\s*\(\d{1,3}\))?|\s+(copia|copy))$", RegexOptions.IgnoreCase)]
    private static partial Regex CopySuffix();

    [GeneratedRegex(@"^(0\d[\s\-.)]+|\d{1,3}(\s+-\s+|\.\s+|\)\s*))(?=\S)")]
    private static partial Regex TrackNumber();
}
