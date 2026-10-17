namespace SoundBoardKing.Core.Storage;

public sealed class AppPaths(string dataDirectory)
{
    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundBoardKing"));

    public string DataDirectory { get; } = dataDirectory;
    public string LibraryFile => Path.Combine(DataDirectory, "library.json");
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string ErrorLogFile => Path.Combine(DataDirectory, "error.log");
}
