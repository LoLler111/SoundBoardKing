using SoundBoardKing.Audio.Speech;

namespace SoundBoardKing.Audio.Tests;

/// <summary>
/// A crash of the graphics card driver closes the app without a chance to catch it: the next start must
/// notice it, once, and only when the app really died while working on the card.
/// </summary>
public class GpuCrashGuardTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sbk-gpu-guard-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Work_that_ends_leaves_no_crash_behind()
    {
        var guard = new GpuCrashGuard(_folder);
        using (guard.Enter())
        {
        }
        Assert.False(new GpuCrashGuard(_folder).TakeCrash());
    }

    [Fact]
    public void Work_that_never_ends_is_a_crash_at_the_next_start_and_only_then()
    {
        // The app dies inside: nothing disposes what Enter returned.
        _ = new GpuCrashGuard(_folder).Enter();

        var nextStart = new GpuCrashGuard(_folder);
        Assert.True(nextStart.TakeCrash());
        Assert.False(nextStart.TakeCrash());
        Assert.False(new GpuCrashGuard(_folder).TakeCrash());
    }

    [Fact]
    public void A_folder_not_there_yet_is_no_crash()
    {
        Assert.False(new GpuCrashGuard(Path.Combine(_folder, "nowhere")).TakeCrash());
    }

    [Fact]
    public void A_text_to_speech_with_nothing_on_the_card_reports_no_crash()
    {
        using var tts = new TextToSpeech(_folder) { UseGpu = true };
        Assert.False(tts.TakeGpuCrash());
    }

    [Fact]
    public void The_timings_of_an_rvc_phrase_say_where_the_time_went()
    {
        string first = TextToSpeech.Timings("Nicola (Kokoro)", 0.62, 4.4, 3.04);
        string next = TextToSpeech.Timings("Nicola (Kokoro)", 0.6, 1.36, 0);
        Assert.Equal($"Nicola (Kokoro) {0.6:0.0} s + RVC {4.4:0.0} s (di cui {3.0:0.0} s per caricare i modelli)", first);
        Assert.Equal($"Nicola (Kokoro) {0.6:0.0} s + RVC {1.4:0.0} s", next);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
