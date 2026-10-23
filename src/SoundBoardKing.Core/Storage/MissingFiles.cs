namespace SoundBoardKing.Core.Storage;

/// <summary>
/// Which sound files are gone. Asking for every file one by one takes seconds on a cold start (and
/// longer on a NAS); a folder holding many of them is listed once instead.
/// </summary>
public static class MissingFiles
{
    // Below this many sounds a folder is not worth listing: it may be Downloads, with thousands of files.
    internal const int ListFolderFrom = 8;

    public static HashSet<string> Find(IEnumerable<string> paths)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in paths.GroupBy(FolderOf, StringComparer.OrdinalIgnoreCase))
        {
            var names = folder.Count() >= ListFolderFrom ? FileNamesIn(folder.Key) : null;
            foreach (var path in folder)
            {
                if (names is null ? !File.Exists(path) : !names.Contains(Path.GetFileName(path)))
                    missing.Add(path);
            }
        }
        return missing;
    }

    private static string FolderOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(path) ?? "";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    // Null when the folder cannot be listed: its files are then asked for one by one.
    private static HashSet<string>? FileNamesIn(string folder)
    {
        if (folder.Length == 0)
            return null;
        try
        {
            return Directory.EnumerateFiles(folder)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
