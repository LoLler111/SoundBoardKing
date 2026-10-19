using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Core.Storage;

public enum ProfileOpenState
{
    Loaded,

    /// <summary>No profile yet: starting empty.</summary>
    Created,

    /// <summary>Built from the library.json and settings.json of earlier versions.</summary>
    Migrated,

    /// <summary>The file the user chose is not there (deleted, or on a drive that is not plugged in).</summary>
    Missing,
}

public sealed record ProfileOpenResult(Profile Profile, ProfileOpenState State);

public enum ProfileLocation
{
    /// <summary>The app data folder: nothing was chosen.</summary>
    Default,

    /// <summary>Next to the executable.</summary>
    NextToApp,

    /// <summary>Near the executable (e.g. a sibling Config folder), through the link file.</summary>
    LinkedFromApp,

    /// <summary>Somewhere else, remembered on this PC only.</summary>
    ThisPc,
}

/// <summary>
/// Reads and writes the profile, a single file holding the library and every setting not tied to
/// this PC's audio devices, which the user can keep anywhere (typically next to the executable).
/// The machine-specific rest, including where the profile is, stays in the app data folder.
/// </summary>
public sealed class ProfileStorage
{
    public const string FileName = "SoundBoardKing.sbk";

    /// <summary>
    /// Text file next to the executable holding the profile's path relative to it, such as
    /// <c>..\Config\SoundBoardKing.sbk</c>, so separate App, Config and Sounds folders can move together.
    /// </summary>
    public const string LinkFileName = "SoundBoardKing.sbklink";

    private readonly AppPaths _paths;
    private readonly JsonFileStore<LocalSettings> _localStore;

    /// <param name="allowAppFolder">
    /// False when the app folder is replaced at every update (installed with Setup.exe): the profile
    /// is then never kept next to the executable nor found through a link file there.
    /// </param>
    public ProfileStorage(AppPaths paths, string executableDirectory, bool allowAppFolder = true)
    {
        _paths = paths;
        AllowsAppFolder = allowAppFolder;
        _localStore = new JsonFileStore<LocalSettings>(paths.LocalSettingsFile);
        ExecutableDirectory = Path.GetFullPath(executableDirectory);
        ExecutableProfilePath = Path.Combine(ExecutableDirectory, FileName);
        LinkFilePath = Path.Combine(ExecutableDirectory, LinkFileName);
        ProfilePath = Resolve();
    }

    public LocalSettings Local { get; private set; } = new();

    /// <summary>The profile file in use.</summary>
    public string ProfilePath { get; private set; }

    /// <summary>How the profile in use was found.</summary>
    public ProfileLocation Location { get; private set; }

    /// <summary>The profile's path relative to the executable's folder, when the link file points to it.</summary>
    public string? RelativeToApp =>
        Location == ProfileLocation.LinkedFromApp ? Path.GetRelativePath(ExecutableDirectory, ProfilePath) : null;

    public string ExecutableDirectory { get; }
    public bool AllowsAppFolder { get; }
    public string ExecutableProfilePath { get; }
    public string LinkFilePath { get; }

    public string DefaultProfilePath => _paths.DefaultProfileFile;

    /// <summary>Reads this PC's settings (taking them from the old settings.json the first time).</summary>
    public void LoadLocal()
    {
        try
        {
            if (File.Exists(_paths.LocalSettingsFile))
                Local = _localStore.Load();
            else if (File.Exists(_paths.LegacySettingsFile))
                Local = new JsonFileStore<LocalSettings>(_paths.LegacySettingsFile).Load(quarantineCorrupt: false);
        }
        finally
        {
            ProfilePath = Resolve();
        }
    }

    private string Resolve()
    {
        var path = Local.ProfilePath is { Length: > 0 } chosen ? chosen
            : !AllowsAppFolder ? _paths.DefaultProfileFile
            : File.Exists(ExecutableProfilePath) ? ExecutableProfilePath
            : LinkedProfilePath() ?? _paths.DefaultProfileFile;
        Location = LocationOf(path);
        return path;
    }

    private ProfileLocation LocationOf(string path) =>
        Local.ProfilePath is { Length: > 0 } ? ProfileLocation.ThisPc
        : SamePath(path, ExecutableProfilePath) ? ProfileLocation.NextToApp
        : SamePath(path, _paths.DefaultProfileFile) ? ProfileLocation.Default
        : ProfileLocation.LinkedFromApp;

    public ProfileOpenResult Open()
    {
        if (File.Exists(ProfilePath))
            return new(Read(ProfilePath, quarantineCorrupt: true), ProfileOpenState.Loaded);
        if (Location is ProfileLocation.ThisPc or ProfileLocation.LinkedFromApp)
            return new(new Profile(), ProfileOpenState.Missing);
        if (!File.Exists(_paths.LegacyLibraryFile) && !File.Exists(_paths.LegacySettingsFile))
            return new(new Profile(), ProfileOpenState.Created);

        var profile = new Profile
        {
            Library = new JsonFileStore<SoundLibrary>(_paths.LegacyLibraryFile).Load(),
            Settings = new JsonFileStore<AppSettings>(_paths.LegacySettingsFile).Load(),
        };
        Save(profile);
        SaveLocal();
        foreach (var legacy in new[] { _paths.LegacyLibraryFile, _paths.LegacySettingsFile })
        {
            if (File.Exists(legacy))
                File.Move(legacy, legacy + ".migrated", overwrite: true);
        }
        return new(profile, ProfileOpenState.Migrated);
    }

    public void Save(Profile profile) => Write(ProfilePath, profile);

    public void SaveLocal() => _localStore.Save(Local);

    /// <summary>Writes the profile to <paramref name="path"/>, uses it from now on and deletes the old file.</summary>
    public void MoveTo(string path, Profile profile)
    {
        path = Path.GetFullPath(path);
        var old = ProfilePath;
        Write(path, profile);
        Remember(path);
        if (!SamePath(old, path) && File.Exists(old))
            File.Delete(old);
    }

    /// <summary>Writes a copy of the profile; the one in use does not change.</summary>
    public void ExportCopy(string path, Profile profile) => Write(Path.GetFullPath(path), profile);

    /// <summary>
    /// Checks that <paramref name="path"/> holds a profile and makes it the one opened at the next start.
    /// Throws <see cref="InvalidDataException"/> (without touching the file) when it does not.
    /// </summary>
    public void Use(string path)
    {
        path = Path.GetFullPath(path);
        var profile = Read(path, quarantineCorrupt: false);
        if (profile.Format != Profile.FormatName)
            throw new InvalidDataException($"{Path.GetFileName(path)} non è un profilo di SoundBoardKing.");
        Remember(path);
    }

    private void Remember(string path)
    {
        // Next to the executable, or near it through the link file, the profile keeps being found
        // when the folders move together (another PC, a USB drive with a different letter).
        if (!AllowsAppFolder)
        {
            Local.ProfilePath = path;
        }
        else if (SamePath(path, ExecutableProfilePath))
        {
            Local.ProfilePath = null;
            DeleteLink();
        }
        else if (NearbyRelativePath(path, ExecutableDirectory) is { } relative && TryWriteLink(relative))
        {
            Local.ProfilePath = null;
        }
        else
        {
            Local.ProfilePath = path;
            DeleteLink();
        }
        ProfilePath = path;
        Location = LocationOf(path);
        SaveLocal();
    }

    private string? LinkedProfilePath()
    {
        try
        {
            if (!File.Exists(LinkFilePath))
                return null;
            var text = File.ReadAllText(LinkFilePath).Trim();
            return text.Length == 0 ? null : Path.GetFullPath(Path.Combine(ExecutableDirectory, text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private bool TryWriteLink(string relativePath)
    {
        try
        {
            File.WriteAllText(LinkFilePath, relativePath + Environment.NewLine);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // e.g. the app is in Program Files: remember the path on this PC only
        }
    }

    private void DeleteLink()
    {
        try
        {
            File.Delete(LinkFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The link only matters when nothing was chosen on this PC, which now it was.
        }
    }

    private static Profile Read(string path, bool quarantineCorrupt)
    {
        var profile = new JsonFileStore<Profile>(path).Load(quarantineCorrupt);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var sound in profile.Library.Sounds)
        {
            if (sound.Path.Length > 0 && !Path.IsPathFullyQualified(sound.Path))
                sound.Path = Path.GetFullPath(Path.Combine(folder, sound.Path));
        }
        return profile;
    }

    private static void Write(string path, Profile profile)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var library = profile.Library;
        var portable = new Profile
        {
            Format = Profile.FormatName,
            Version = profile.Version,
            Settings = profile.Settings,
            Library = new SoundLibrary
            {
                Version = library.Version,
                Sounds = library.Sounds.Select(s => s.WithPath(PortablePath(s.Path, folder))).ToList(),
                Categories = library.Categories,
                Hotbar = library.Hotbar,
                Sort = library.Sort,
            },
        };
        new JsonFileStore<Profile>(path).Save(portable);
    }

    // Sounds near the profile are stored relative to it, so they are still found after the folders
    // are moved or copied together.
    private static string PortablePath(string soundPath, string folder) =>
        Path.IsPathFullyQualified(soundPath) ? NearbyRelativePath(soundPath, folder) ?? soundPath : soundPath;

    /// <summary>
    /// The path relative to <paramref name="folder"/> when it lies inside it or inside a sibling
    /// folder (e.g. App\, Config\ and Sounds\ side by side); otherwise null.
    /// </summary>
    internal static string? NearbyRelativePath(string fullPath, string folder)
    {
        var relative = Path.GetRelativePath(folder, fullPath);
        if (Path.IsPathRooted(relative))
            return null; // another drive
        var parts = relative.Split(Path.DirectorySeparatorChar);
        int levelsUp = parts.TakeWhile(p => p == "..").Count();
        return levelsUp <= 1 && levelsUp < parts.Length ? relative : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
