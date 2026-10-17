namespace SoundBoardKing.Core.Import;

/// <summary>
/// Finds the audio file a SoundPad list points to. Lists are often moved away from the sounds
/// (or carry absolute paths from another PC), so after the literal path it tries nearby folders
/// and finally a lookup by file name.
/// </summary>
internal sealed class SoundPathResolver(string baseDirectory)
{
    private const int ParentLevelsToTry = 2;
    private const int MaxIndexedFiles = 50_000;

    private Dictionary<string, string>? _byFileName;

    public string Resolve(string url, out bool found)
    {
        found = false;
        url = url.Trim();
        if (url.Length == 0)
            return "";

        if (Path.IsPathRooted(url))
        {
            if (File.Exists(url))
            {
                found = true;
                return url;
            }
        }
        else
        {
            var directory = new DirectoryInfo(baseDirectory);
            for (int level = 0; level <= ParentLevelsToTry && directory is not null; level++, directory = directory.Parent)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.FullName, url));
                if (File.Exists(candidate))
                {
                    found = true;
                    return candidate;
                }
            }
        }

        var byName = FindByFileName(Path.GetFileName(url));
        if (byName is not null)
        {
            found = true;
            return byName;
        }

        return Path.IsPathRooted(url) ? url : Path.GetFullPath(Path.Combine(baseDirectory, url));
    }

    private string? FindByFileName(string fileName)
    {
        if (fileName.Length == 0)
            return null;
        _byFileName ??= BuildIndex();
        return _byFileName.GetValueOrDefault(fileName);
    }

    private Dictionary<string, string> BuildIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var searchRoot = Directory.GetParent(baseDirectory)?.FullName ?? baseDirectory;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };

        int count = 0;
        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", options))
        {
            index.TryAdd(Path.GetFileName(file), file);
            if (++count >= MaxIndexedFiles)
                break;
        }
        return index;
    }
}
