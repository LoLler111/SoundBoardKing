using System.Diagnostics;

namespace SoundBoardKing.MicSetup;

/// <summary>Restarts the Windows audio service so audiodg.exe reloads the effects chain.</summary>
internal static class AudioService
{
    public static void Stop(Action<string> log) => Net("stop Audiosrv /y", log);

    public static void Start(Action<string> log) => Net("start Audiosrv", log);

    public static void Restart(Action<string> log)
    {
        Stop(log);
        Start(log);
    }

    // "net" waits for the service to reach the new state, unlike "sc". Exit code 2 means it
    // already was in that state, which is fine.
    private static void Net(string arguments, Action<string> log)
    {
        using var process = Process.Start(new ProcessStartInfo("net.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        log($"net {arguments} -> {process.ExitCode}{(error.Length > 0 ? " " + error : "")}");
        if (process.ExitCode is not (0 or 2))
            throw new InvalidOperationException($"Impossibile eseguire \"net {arguments}\": {error}");
    }
}
