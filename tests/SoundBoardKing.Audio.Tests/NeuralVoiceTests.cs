using NAudio.Wave;
using SoundBoardKing.Audio.Speech;

namespace SoundBoardKing.Audio.Tests;

/// <summary>
/// The neural voices need hundreds of MB of models: these tests run only where SBK_TTS_VOICES names a folder
/// with them (SBK_TTS_DOWNLOAD=1 downloads them there first, through the app's own catalog).
/// </summary>
public class NeuralVoiceTests
{
    private static readonly string? Folder = Environment.GetEnvironmentVariable("SBK_TTS_VOICES");

    private const string Announcement = "Il treno regionale delle ore 18:35, per Milano Centrale, è in arrivo al binario 3.";

    [Fact]
    public async Task The_catalog_downloads_and_checks_every_package()
    {
        if (Folder is null || Environment.GetEnvironmentVariable("SBK_TTS_DOWNLOAD") != "1")
            return;
        var catalog = new VoiceCatalog(Folder);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        foreach (var package in VoiceCatalog.Packages)
        {
            double last = 0;
            await catalog.DownloadAsync(package, http, new SyncProgress(p => last = p), CancellationToken.None);
            Assert.True(catalog.IsInstalled(package), package.Id);
            Assert.Equal(1, last);
            Assert.Equal(0, catalog.DownloadSize(package));
        }
    }

    [Fact]
    public void ESpeak_phonemizes_as_piper_does()
    {
        if (Folder is null)
            return;
        using var espeak = new ESpeak(new VoiceCatalog(Folder).ESpeakFolder);
        // What piper.exe 2023.11.14-2 logs for the same text (--debug).
        Assert.Equal(["il trˈɛno redʒonˈale dˌelle ˈore ditʃˈɔtːo e trˈeːntatʃˈinkwe, per milˈano tʃentrˈale, e in arɾˈivo al binˈario trˈe."],
            espeak.Phonemize(Announcement, "it"));
        Assert.Equal(2, espeak.Phonemize("Ciao! Come stai?", "it").Count);
        // A line break without punctuation still ends the sentence.
        Assert.Equal(2, espeak.Phonemize("prima riga\nseconda riga", "it").Count);
        Assert.StartsWith("həlˈoʊ", espeak.Phonemize("Hello", "en-us")[0]);
    }

    [Fact]
    public void Piper_phoneme_ids_match_piper_exe()
    {
        if (Folder is null)
            return;
        var catalog = new VoiceCatalog(Folder);
        var paola = VoiceCatalog.Packages.Single(p => p.Id == "piper-paola");
        if (!catalog.IsInstalled(paola))
            return;
        using var espeak = new ESpeak(catalog.ESpeakFolder);
        using var model = new PiperModel(catalog.PathOf(paola, paola.Files[0]));
        long[] ids = model.PhonemeIds(espeak.Phonemize(Announcement, "it")[0]);
        // The first and last ids piper.exe logged for this text, and its count.
        Assert.Equal(237, ids.Length);
        Assert.Equal([1, 0, 21, 0, 24, 0, 3, 0, 32, 0, 30, 0, 120, 0, 61, 0], ids[..16]);
        Assert.Equal([3, 0, 32, 0, 30, 0, 120, 0, 18, 0, 10, 0, 2], ids[^13..]);
    }

    [Fact]
    public async Task Every_downloaded_neural_voice_speaks()
    {
        if (Folder is null)
            return;
        using var tts = new TextToSpeech(Folder);
        string output = Path.Combine(Path.GetTempPath(), "sbk-neural-tests");
        var voices = (await tts.GetVoicesAsync()).Where(v => v.Engine != TtsEngine.Windows).ToList();
        foreach (var voice in voices)
        {
            string normal = await tts.SynthesizeAsync(Announcement + " Attenzione! Allontanarsi dalla linea gialla.", voice, 0, output);
            string fast = await tts.SynthesizeAsync(Announcement + " Attenzione! Allontanarsi dalla linea gialla.", voice, 10, output);
            var length = Duration(normal);
            Assert.True(length.TotalSeconds is > 4 and < 15, $"{voice.Name}: {length}");
            // Piper predicts durations with some randomness: twice as fast is never exactly half as long.
            Assert.True(Duration(fast) < length * 0.85, $"{voice.Name}: {length} / {Duration(fast)}");
            // Kept for listening to; the folder is in the temp files.
            File.Copy(normal, Path.Combine(output, voice.Name + ".wav"), overwrite: true);
        }
    }

    private static TimeSpan Duration(string path)
    {
        using var reader = new WaveFileReader(path);
        return reader.TotalTime;
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
