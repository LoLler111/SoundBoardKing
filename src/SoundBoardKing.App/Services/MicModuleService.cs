using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SoundBoardKing.App.Services;

public sealed record MicSetupResult(bool Success, string Message);

/// <summary>
/// Installs the mic module through SoundBoardKing.MicSetup.exe, which runs elevated (UAC),
/// and reads which microphones it is installed on.
/// </summary>
public static class MicModuleService
{
    private const string EndpointsKey = @"SOFTWARE\SoundBoardKing\MicFx\Endpoints";
    private static string SetupPath => Path.Combine(AppContext.BaseDirectory, "SoundBoardKing.MicSetup.exe");

    public static bool IsSetupAvailable =>
        File.Exists(SetupPath) && File.Exists(Path.Combine(AppContext.BaseDirectory, "SbkMicFx.dll"));

    /// <summary>Endpoint ids the module is installed on, with the chain position used.</summary>
    public static IReadOnlyDictionary<string, string> InstalledDevices()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var endpoints = Registry.LocalMachine.OpenSubKey(EndpointsKey);
        if (endpoints is null)
            return result;
        foreach (var guid in endpoints.GetSubKeyNames())
        {
            using var endpoint = endpoints.OpenSubKey(guid);
            if (endpoint?.GetValue("DeviceId") is string deviceId)
                result[deviceId] = endpoint.GetValue("Slot") as string ?? "";
        }
        return result;
    }

    public static Task<MicSetupResult> InstallAsync(string deviceId, string slot) =>
        RunAsync($"install --device \"{deviceId}\" --slot {slot}");

    public static Task<MicSetupResult> UninstallAsync(string deviceId) =>
        RunAsync($"uninstall --device \"{deviceId}\"");

    /// <summary>Copies the SbkMicFx.dll shipped with this version over the installed one.</summary>
    public static Task<MicSetupResult> RefreshAsync() => RunAsync("refresh");

    /// <summary>
    /// True when the module is installed on some microphone but from a different SbkMicFx.dll than
    /// the one next to the app: the app was updated and the copy audiodg.exe loads is the old one.
    /// </summary>
    public static bool NeedsRefresh()
    {
        try
        {
            if (!IsSetupAvailable || InstalledDevices().Count == 0)
                return false;
            using var state = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SoundBoardKing\MicFx");
            if (state?.GetValue("InstallPath") is not string installed || !File.Exists(installed))
                return true;
            var bundled = Path.Combine(AppContext.BaseDirectory, "SbkMicFx.dll");
            return !File.ReadAllBytes(installed).AsSpan().SequenceEqual(File.ReadAllBytes(bundled));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the module from every microphone while the app itself is being uninstalled, so no
    /// effect is left pointing at a removed app. Waits a limited time: the uninstaller is waiting too.
    /// </summary>
    public static void UninstallAllForAppRemoval()
    {
        if (!IsSetupAvailable || InstalledDevices().Count == 0)
            return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(SetupPath, "uninstall --all")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(TimeSpan.FromSeconds(25));
        }
        catch (Win32Exception)
        {
            // Declined: the module stays and can be removed by reinstalling and using Settings.
        }
    }

    private static async Task<MicSetupResult> RunAsync(string arguments)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), $"sbk-micsetup-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(SetupPath, $"{arguments} --result \"{resultFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            await process.WaitForExitAsync();

            if (!File.Exists(resultFile))
                return new MicSetupResult(false, $"L'installazione è terminata senza risultato (codice {process.ExitCode}).");
            var lines = await File.ReadAllLinesAsync(resultFile);
            return new MicSetupResult(lines.FirstOrDefault() == "OK", string.Join(Environment.NewLine, lines.Skip(1)));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new MicSetupResult(false, "Operazione annullata: serve il permesso di amministratore.");
        }
        finally
        {
            File.Delete(resultFile);
        }
    }
}
