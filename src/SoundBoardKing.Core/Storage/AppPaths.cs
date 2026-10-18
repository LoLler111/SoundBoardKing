namespace SoundBoardKing.Core.Storage;

public sealed class AppPaths(string dataDirectory)
{
    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundBoardKing"));

    public string DataDirectory { get; } = dataDirectory;

    /// <summary>Settings tied to this PC (audio devices, profile location).</summary>
    public string LocalSettingsFile => Path.Combine(DataDirectory, "local.json");

    /// <summary>Where the profile lives until the user chooses another place.</summary>
    public string DefaultProfileFile => Path.Combine(DataDirectory, ProfileStorage.FileName);

    /// <summary>Library and settings files used before the profile existed; migrated on first start.</summary>
    public string LegacyLibraryFile => Path.Combine(DataDirectory, "library.json");
    public string LegacySettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string ErrorLogFile => Path.Combine(DataDirectory, "error.log");
}
