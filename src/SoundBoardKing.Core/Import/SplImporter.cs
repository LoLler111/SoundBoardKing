using System.Globalization;
using System.Xml.Linq;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Core.Import;

public sealed record SplImportResult(SoundLibrary Library, int MissingFiles, IReadOnlyList<string> Warnings);

/// <summary>Reads a SoundPad sound list (.spl): sounds, nested categories and the hotbar.</summary>
public static class SplImporter
{
    public static SplImportResult Import(string splPath)
    {
        var fullPath = Path.GetFullPath(splPath);
        var root = XDocument.Load(fullPath).Root;
        if (root is null || root.Name.LocalName != "Soundlist")
            throw new InvalidDataException("Il file non è una lista suoni di SoundPad.");

        var warnings = new List<string>();
        var resolver = new SoundPathResolver(Path.GetDirectoryName(fullPath)!);
        var library = new SoundLibrary();
        var idsByIndex = new Dictionary<int, Guid>();
        int missing = 0;

        // SoundPad identifies a sound by its 1-based position in the list.
        int index = 0;
        foreach (var element in root.Elements("Sound"))
        {
            index++;
            var url = (string?)element.Attribute("url") ?? "";
            var path = resolver.Resolve(url, out bool found);
            if (!found)
                missing++;

            var title = (string?)element.Attribute("title");
            var sound = new Sound
            {
                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(url) : title,
                Artist = (string?)element.Attribute("artist") ?? "",
                Path = path,
                DurationMs = ParseDuration((string?)element.Attribute("duration")),
                AddedOn = ParseDate((string?)element.Attribute("addedOn")) ?? DateOnly.FromDateTime(DateTime.Today),
            };
            library.Sounds.Add(sound);
            idsByIndex[index] = sound.Id;
        }

        foreach (var element in root.Element("Categories")?.Elements("Category") ?? [])
        {
            // Categories with a "type" are SoundPad built-ins such as "All sounds".
            if (element.Attribute("type") is not null)
                continue;
            library.Categories.Add(ReadCategory(element, idsByIndex, warnings));
        }

        int pageIndex = 0;
        foreach (var page in root.Element("Hotbar")?.Elements("HotbarPage") ?? [])
        {
            foreach (var item in page.Elements("HotbarItem"))
            {
                if (!TryParseInt((string?)item.Attribute("id"), out int id) ||
                    !TryParseInt((string?)item.Attribute("soundId"), out int soundIndex))
                    continue;

                // Slot ids are page * 1000 + slot; lists with one HotbarPage element per page use plain slot ids.
                int pageNumber = id >= 1000 ? id / 1000 : pageIndex;
                int slot = id % 1000;
                if (!Hotbar.IsValid(pageNumber, slot))
                {
                    warnings.Add($"Slot dei preferiti {id} fuori intervallo, ignorato.");
                    continue;
                }
                if (!idsByIndex.TryGetValue(soundIndex, out var soundId))
                {
                    warnings.Add($"Lo slot dei preferiti {id} punta al suono {soundIndex}, che non esiste.");
                    continue;
                }
                library.Hotbar.Set(pageNumber, slot, soundId);
            }
            pageIndex++;
        }

        return new SplImportResult(library, missing, warnings);
    }

    private static Category ReadCategory(XElement element, Dictionary<int, Guid> idsByIndex, List<string> warnings)
    {
        var category = new Category
        {
            Name = (string?)element.Attribute("name") ?? "",
            Icon = (string?)element.Attribute("icon") ?? "",
        };

        foreach (var reference in element.Elements("Sound"))
        {
            if (TryParseInt((string?)reference.Attribute("id"), out int soundIndex) &&
                idsByIndex.TryGetValue(soundIndex, out var soundId))
            {
                if (!category.SoundIds.Contains(soundId))
                    category.SoundIds.Add(soundId);
            }
            else
            {
                warnings.Add($"La categoria \"{category.Name}\" contiene un suono inesistente ({(string?)reference.Attribute("id")}).");
            }
        }

        foreach (var child in element.Elements("Category"))
            category.Children.Add(ReadCategory(child, idsByIndex, warnings));

        return category;
    }

    /// <summary>Parses "m:ss" or "h:mm:ss" into milliseconds; 0 when unreadable.</summary>
    internal static long ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        long seconds = 0;
        foreach (var part in text.Trim().Split(':'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                return 0;
            seconds = seconds * 60 + value;
        }
        return seconds * 1000;
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static bool TryParseInt(string? text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
