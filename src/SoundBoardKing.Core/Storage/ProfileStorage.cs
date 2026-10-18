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

/// <summary>
/// Reads and writes the profile, a single file holding the library and every setting not tied to
/// this PC's audio devices, which the user can keep anywhere (typically next to the executable).
/// The machine-specific rest, including where the profile is, stays in the app data folder.
/// </summary>
public sealed class ProfileStorage
{
    public const string FileName = "SoundBoardKing.sbk";

    private readonly AppPaths _paths;
    private readonly JsonFileStore<LocalSettings> _localStore;

    public ProfileStorage(AppPaths paths, string executableDirectory)
    {
        _paths = paths;
        _localStore = new JsonFileStore<LocalSettings>(paths.LocalSettingsFile);
        ExecutableProfilePath = Path.Combine(Path.GetFullPath(executableDirectory), FileName);
        ProfilePath = Resolve();
    }

    public LocalSettings Local { get; private set; } = new();

    /// <summary>The profile file in use.</summary>
    public string ProfilePath { get; private set; }

    public string ExecutableProfilePath { get; }

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

    private string Resolve() =>
        Local.ProfilePath is { Length: > 0 } chosen ? chosen
        : File.Exists(ExecutableProfilePath) ? ExecutableProfilePath
        : _paths.DefaultProfileFile;

    public ProfileOpenResult Open()
    {
        if (File.Exists(ProfilePath))
            return new(Read(ProfilePath, quarantineCorrupt: true), ProfileOpenState.Loaded);
        if (Local.ProfilePath is { Length: > 0 })
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
        // Next to the executable is the automatic choice, so the profile keeps being found when
        // the whole folder moves (another PC, a USB drive with a different letter).
        Local.ProfilePath = SamePath(path, ExecutableProfilePath) ? null : path;
        ProfilePath = path;
        SaveLocal();
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
            },
        };
        new JsonFileStore<Profile>(path).Save(portable);
    }

    // Sounds inside the profile's folder are stored relative to it, so they are still found after
    // the folder is copied elsewhere.
    private static string PortablePath(string soundPath, string folder)
    {
        if (!Path.IsPathFullyQualified(soundPath))
            return soundPath;
        var relative = Path.GetRelativePath(folder, soundPath);
        bool outside = Path.IsPathRooted(relative) || relative == ".." ||
                       relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return outside ? soundPath : relative;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
