using NAudio.Wave;
using SoundBoardKing.Audio.Speech;

namespace SoundBoardKing.Audio.Tests;

/// <summary>
/// With "graphics card" on, the voices speak on any PC. Kokoro stays on the processor even then: on the
/// graphics card (DirectML) it was slower and crashed the app on AMD cards. Needs the downloaded voices
/// (SBK_TTS_VOICES).
/// </summary>
public class GpuTests
{
    [Fact]
    public async Task Asking_for_the_graphics_card_keeps_kokoro_on_the_processor()
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
        Assert.False(tts.LastOnGpu);
        Assert.Equal("processore", tts.LastDevice);
        Console.WriteLine($"Nicola: {tts.LastDevice}, {watch.Elapsed.TotalSeconds:0.0} s");
    }
}
