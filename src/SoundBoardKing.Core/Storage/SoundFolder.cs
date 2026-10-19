using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Core.Storage;

/// <summary>What looking for new sounds found in the sounds folder.</summary>
/// <param name="NewFiles">Audio files the library does not have yet.</param>
/// <param name="Relinked">Sounds whose file was missing, with the file of the same name found in the folder.</param>
public sealed record SoundFolderScan(IReadOnlyList<string> NewFiles, IReadOnlyList<(Sound Sound, string Path)> Relinked);

/// <summary>
/// The folder holding all the sound files (<see cref="AppSettings.SoundsFolder"/>), chosen either
/// relative to the app's folder, so it moves together with the app, or as a fixed path such as a NAS share.
/// </summary>
public static class SoundFolder
{
    /// <summary>The full path of the chosen folder; null when none is chosen or it is not a valid path.</summary>
    public static string? Resolve(string? setting, string appFolder)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return null;
        try
        {
            // A rooted setting (C:\…, \\NAS\…) wins over the app folder in Combine.
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(appFolder, setting.Trim())));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The folder relative to the app's folder; null when that is not possible (another drive or share).</summary>
    public static string? RelativeToApp(string fullPath, string appFolder)
    {
        var relative = Path.GetRelativePath(appFolder, fullPath);
        return Path.IsPathRooted(relative) ? null : relative;
    }

    public static bool Contains(string folder, string path) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="folder"/> as <paramref name="relativeTarget"/>
    /// and returns where it ended up. A file already inside the folder stays where it is; one with
    /// the same name and size already there is taken as the same sound and reused; a different one
    /// with that name gets " (2)", " (3)"… .
    /// </summary>
    public static string CopyIn(string source, string folder, string relativeTarget)
    {
        if (Contains(folder, source))
            return Path.GetFullPath(source);

        var target = Path.GetFullPath(Path.Combine(folder, relativeTarget));
        var directory = Path.GetDirectoryName(target)!;
        var name = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        long size = new FileInfo(source).Length;
        for (int n = 2; File.Exists(target); n++)
        {
            if (new FileInfo(target).Length == size)
                return target;
            target = Path.Combine(directory, $"{name} ({n}){extension}");
        }
        Directory.CreateDirectory(directory);
        File.Copy(source, target);
        return target;
    }

    /// <summary>
    /// Looks through <paramref name="folder"/> and its subfolders for audio files that are not in
    /// <paramref name="sounds"/>. A sound whose file is missing is relinked to a file with the same
    /// name instead of being added twice; <paramref name="excluded"/> files (relative to the folder,
    /// removed from the library on purpose) are skipped.
    /// </summary>
    public static SoundFolderScan Scan(string folder, IReadOnlyCollection<Sound> sounds, IEnumerable<string> excluded,
        Func<string, bool> isAudio)
    {
        var known = sounds.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingByName = sounds
            .Where(s => !File.Exists(s.Path))
            .GroupBy(s => Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new Queue<Sound>(g), StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var files = Directory.EnumerateFiles(folder, "*", options)
            .Where(isAudio)
            .Order(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var relinked = new List<(Sound, string)>();
        foreach (var file in files)
        {
            if (known.Contains(file) || skipped.Contains(Path.GetRelativePath(folder, file)))
                continue;
            if (missingByName.TryGetValue(Path.GetFileName(file), out var waiting) && waiting.TryDequeue(out var sound))
                relinked.Add((sound, file));
            else
                added.Add(file);
        }
        return new SoundFolderScan(added, relinked);
    }
}
