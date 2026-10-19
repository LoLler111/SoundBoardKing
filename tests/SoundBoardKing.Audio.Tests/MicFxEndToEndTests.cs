using System.Diagnostics;
using NAudio.Wave.SampleProviders;
using SoundBoardKing.Audio.MicFx;
using Xunit.Abstractions;

namespace SoundBoardKing.Audio.Tests;

/// <summary>
/// Runs the real APO code (compiled into MicFxHarness.exe with a per-session shared memory
/// name) against the real <see cref="ApoMicFeed"/>. Needs build-native.ps1 -Tests first;
/// without the harness the test returns early.
/// </summary>
public class MicFxEndToEndTests(ITestOutputHelper output)
{
    private const string TestSharedMemoryName = @"Local\SoundBoardKing.MicFx.test";

    private static string? FindHarness()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "native-tests", "x64", "MicFxHarness.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    [Fact]
    public async Task Sound_written_by_the_app_is_mixed_into_the_microphone()
    {
        var harness = FindHarness();
        if (harness is null)
        {
            output.WriteLine("MicFxHarness.exe non trovato: esegui build-native.ps1 -Tests.");
            return;
        }

        using var process = Process.Start(new ProcessStartInfo(harness, "15")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        // The harness creates the shared memory in LockForProcess, then prints READY.
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync()) is not null && line != "READY")
            output.WriteLine(line);
        Assert.Equal("READY", line);

        using var feed = new ApoMicFeed(TestSharedMemoryName);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!feed.IsAvailable && DateTime.UtcNow < deadline)
            await Task.Delay(200);
        Assert.True(feed.IsAvailable, "the APO never reported as active");

        var status = feed.GetStatus();
        Assert.Equal(48000, status.SampleRate);
        Assert.Equal(2, status.Channels);

        var sine = new SignalGenerator(feed.Format!.SampleRate, 1)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = 440,
            Gain = 0.5,
        };
        feed.Attach(sine);
        // Robot on the voice, refreshed like the app does every second: the harness checks the APO reads it.
        using var heartbeat = new Timer(_ => feed.SetVoiceEffect(3, 0.8f), null, 0, 500);

        var rest = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        feed.Detach();
        output.WriteLine(rest);

        Assert.Contains("PASS", rest);
        Assert.Equal(0, process.ExitCode);
    }
}
