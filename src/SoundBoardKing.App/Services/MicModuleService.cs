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
