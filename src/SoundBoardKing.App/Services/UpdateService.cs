using System.Reflection;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace SoundBoardKing.App.Services;

/// <summary>
/// Updates from the GitHub releases of the public repository through Velopack. Works only when the
/// app was installed with Setup.exe or unpacked from the Portable zip; a plain build folder (or
/// <c>dotnet run</c>) never updates itself.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/LoLler111/SoundBoardKing";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _downloaded;

    public UpdateService()
    {
        try
        {
            // A folder or web address holding the output of publish.ps1 (artifacts\releases) instead of
            // GitHub: lets an installed copy be tried against a release that is not published yet.
            var testSource = Environment.GetEnvironmentVariable("SOUNDBOARDKING_UPDATE_SOURCE");
            _manager = string.IsNullOrWhiteSpace(testSource)
                ? new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false))
                : new UpdateManager(testSource);
        }
        catch (Exception)
        {
            _manager = null; // no Velopack layout around the exe
        }
    }

    /// <summary>Version of this build, from version.txt (e.g. "0.2.0").</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>Installed with Setup.exe or unpacked from the Portable zip: can update itself.</summary>
    public bool CanUpdate => _manager is { IsInstalled: true };

    /// <summary>Installed with Setup.exe (not the Portable zip): its folder is replaced at every update.</summary>
    public bool IsSetupInstall => _manager is { IsInstalled: true, IsPortable: false };

    /// <summary>
    /// Folder holding the Velopack stub exe the user starts, which survives updates (the app itself
    /// runs from its "current" subfolder, replaced each time). Null when not unpacked from the Portable zip.
    /// </summary>
    public static string? PortableRootDirectory =>
        VelopackLocator.IsCurrentSet && VelopackLocator.Current is { IsPortable: true, RootAppDir: { } root } ? root : null;

    /// <summary>The version downloaded and waiting to be installed, if any.</summary>
    public string? ReadyVersion => _downloaded?.TargetFullRelease.Version.ToString();

    /// <summary>Downloads the newest release if there is one; returns its version, or null when up to date.</summary>
    public async Task<string?> DownloadLatestAsync(Action<int>? progress = null)
    {
        if (_manager is not { IsInstalled: true } manager)
            return null;
        if (_downloaded is not null)
            return ReadyVersion;
        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
            return null;
        await manager.DownloadUpdatesAsync(update, progress).ConfigureAwait(false);
        _downloaded = update;
        return ReadyVersion;
    }

    /// <summary>
    /// Hands the downloaded update to Velopack's updater, which installs it as soon as this process
    /// exits (and starts the app again when <paramref name="restart"/>). False when nothing is ready.
    /// </summary>
    public bool ApplyAfterExit(bool restart)
    {
        if (_manager is null || _downloaded is null)
            return false;
        _manager.WaitExitThenApplyUpdates(_downloaded.TargetFullRelease, silent: true, restart, []);
        _downloaded = null;
        return true;
    }

    private static string ReadCurrentVersion()
    {
        var version = typeof(UpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int metadata = version.IndexOf('+');
        return metadata < 0 ? version : version[..metadata];
    }
}
