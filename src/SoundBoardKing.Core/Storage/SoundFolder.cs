using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Core.Storage;

/// <summary>What looking for new sounds found in the sounds folder.</summary>
/// <param name="NewFiles">Audio files the library does not have yet.</param>
/// <param name="Relinked">Sounds whose file was missing, or that sit outside a folder just chosen, with their file found there.</param>
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
    /// <paramref name="sounds"/>. A sound whose file is missing is relinked to its file found there
    /// instead of being added twice; <paramref name="excluded"/> files (relative to the folder,
    /// removed from the library on purpose) are skipped.
    /// </summary>
    /// <param name="folderChanged">
    /// The folder was just chosen: every sound outside it is relinked to its copy found there, even
    /// when the old file is still in place (the folder was copied rather than moved).
    /// </param>
    /// <param name="previousFolder">The folder used before, whose subfolders the new one is expected to mirror.</param>
    public static SoundFolderScan Scan(string folder, IReadOnlyCollection<Sound> sounds, IEnumerable<string> excluded,
        Func<string, bool> isAudio, bool folderChanged = false, string? previousFolder = null)
    {
        var known = sounds.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var files = Directory.EnumerateFiles(folder, "*", options)
            .Where(f => !known.Contains(f) && !skipped.Contains(Path.GetRelativePath(folder, f)) && isAudio(f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var free = files.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wanted = sounds
            .Where(s => s.Path.Length > 0)
            .Select(s => (Sound: s, Size: File.Exists(s.Path) ? new FileInfo(s.Path).Length : -1))
            .Where(w => w.Size < 0 || (folderChanged && !Contains(folder, w.Sound.Path)))
            .ToList();
        // A file still in place is only swapped for an identical copy.
        bool Fits((Sound Sound, long Size) w, string file) =>
            free.Contains(file) && (w.Size < 0 || new FileInfo(file).Length == w.Size);

        var relinked = new List<(Sound, string)>();
        void Relink((Sound Sound, long Size) w, string file)
        {
            relinked.Add((w.Sound, file));
            free.Remove(file);
            wanted.Remove(w);
        }

        // First the same place in the moved folder, so the whole structure follows it…
        if (previousFolder is not null)
        {
            foreach (var w in wanted.Where(w => Contains(previousFolder, w.Sound.Path)).ToList())
            {
                var file = Path.Combine(folder, Path.GetRelativePath(previousFolder, w.Sound.Path));
                if (Fits(w, file))
                    Relink(w, file);
            }
        }
        // …then a file with the same name, preferring the one under the same subfolders.
        var byName = files.ToLookup(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        foreach (var w in wanted.ToList())
        {
            var file = byName[Path.GetFileName(w.Sound.Path)]
                .Where(f => Fits(w, f))
                .MaxBy(f => SharedFolders(w.Sound.Path, f));
            if (file is not null)
                Relink(w, file);
        }

        return new SoundFolderScan(files.Where(free.Contains).ToList(), relinked);
    }

    /// <summary>How many folders, going up from the file, the two paths have in common (Meme\airhorn.mp3: 1).</summary>
    private static int SharedFolders(string a, string b)
    {
        var x = Path.GetDirectoryName(a)?.Split(Path.DirectorySeparatorChar) ?? [];
        var y = Path.GetDirectoryName(b)?.Split(Path.DirectorySeparatorChar) ?? [];
        int n = 0;
        while (n < x.Length && n < y.Length && string.Equals(x[^(n + 1)], y[^(n + 1)], StringComparison.OrdinalIgnoreCase))
            n++;
        return n;
    }
}
