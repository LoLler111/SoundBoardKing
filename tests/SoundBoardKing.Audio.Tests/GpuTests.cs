using NAudio.Wave;
using SoundBoardKing.Audio.Speech;

namespace SoundBoardKing.Audio.Tests;

/// <summary>
/// With "graphics card" on, the voices speak on any PC: on the card through DirectML where there is a
/// DirectX 12 one, on the processor otherwise. Needs the downloaded voices (SBK_TTS_VOICES).
/// </summary>
public class GpuTests
{
    [Fact]
    public async Task Asking_for_the_graphics_card_still_speaks_wherever_it_runs()
    {
        string? voices = Environment.GetEnvironmentVariable("SBK_TTS_VOICES");
        if (voices is null)
            return;
        using var tts = new TextToSpeech(voices) { UseGpu = true };
        var nicola = (await tts.GetVoicesAsync()).FirstOrDefault(v => v.Name == "Nicola (Kokoro)");
        if (nicola is null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "sbk-gpu-tests");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string path = await tts.SynthesizeAsync("Il treno è in arrivo al binario tre.", nicola, 0, folder);
        watch.Stop();
        using var reader = new WaveFileReader(path);
        Assert.True(reader.TotalTime > TimeSpan.FromSeconds(1));
        Console.WriteLine($"Nicola: {(tts.LastOnGpu ? "scheda video" : "processore")}, {watch.Elapsed.TotalSeconds:0.0} s; problema: {tts.GpuProblem ?? "nessuno"}");
    }
}
