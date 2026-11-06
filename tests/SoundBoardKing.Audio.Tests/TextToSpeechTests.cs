using NAudio.Wave;
using SoundBoardKing.Audio.Speech;

namespace SoundBoardKing.Audio.Tests;

// Uses the voices of the PC running the tests; with none installed there is nothing to check.
public class TextToSpeechTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sbk-tts-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TextToSpeech _tts;

    public TextToSpeechTests() => _tts = new TextToSpeech(Path.Combine(_folder, "voices"));

    private async Task<List<TtsVoice>> WindowsVoices() =>
        (await _tts.GetVoicesAsync()).Where(v => v.Engine == TtsEngine.Windows).ToList();

    [Fact]
    public async Task Every_windows_voice_speaks_into_a_wave_file()
    {
        var voices = await WindowsVoices();
        Assert.Equal(voices.Count, voices.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var voice in voices)
        {
            string path = await _tts.SynthesizeAsync("Il treno è in arrivo al binario tre.", voice, 0, _folder);
            Assert.True(Duration(path) > TimeSpan.FromSeconds(0.5), $"{voice.Name}: {Duration(path)}");
        }
    }

    [Fact]
    public async Task A_faster_rate_makes_a_shorter_file()
    {
        var voice = (await WindowsVoices()).FirstOrDefault(v => !v.Is32Bit);
        if (voice is null)
            return;
        const string text = "Attenzione, allontanarsi dalla linea gialla.";
        var slow = Duration(await _tts.SynthesizeAsync(text, voice, -5, _folder));
        var fast = Duration(await _tts.SynthesizeAsync(text, voice, 5, _folder));
        Assert.True(fast < slow * 0.8, $"lento {slow}, veloce {fast}");
    }

    [Fact]
    public async Task Text_that_looks_like_markup_is_read_not_parsed()
    {
        var voice = (await WindowsVoices()).FirstOrDefault(v => !v.Is32Bit);
        if (voice is null)
            return;
        // As SAPI XML this would be a silent pause; read as text it says the words.
        var path = await _tts.SynthesizeAsync("<silence msec=\"10\"/> ciao", voice, 0, _folder);
        Assert.True(Duration(path) > TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public async Task A_32_bit_voice_speaks_through_the_32_bit_powershell()
    {
        // Any voice also installed for 32-bit programs, forced down that path.
        var voice = (await WindowsVoices()).FirstOrDefault(v => v.Id.Contains(@"\Speech\Voices\Tokens\"));
        if (voice is null)
            return;
        var as32 = voice with { Id = voice.Id[(voice.Id.LastIndexOf('\\') + 1)..], Is32Bit = true };
        var path = await _tts.SynthesizeAsync("Prova a trentadue bit, con l'apostrofo.", as32, 0, _folder);
        Assert.True(Duration(path) > TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void Languages_and_genders_read_in_italian_or_in_the_voice_language()
    {
        Assert.Equal("Italiano (Italia)", SapiSpeech.LanguageName("410"));
        Assert.Equal("English (United States)", SapiSpeech.LanguageName("409;9"));
        Assert.Equal("", SapiSpeech.LanguageName(null));
        Assert.Equal("maschile", SapiSpeech.GenderName("Male"));
    }

    [Fact]
    public async Task Neural_voices_appear_only_once_downloaded()
    {
        Assert.DoesNotContain(await _tts.GetVoicesAsync(), v => v.Engine != TtsEngine.Windows);
        Assert.All(VoiceCatalog.Packages, p => Assert.False(_tts.Catalog.IsInstalled(p)));
        // eSpeak is counted with the first voice only.
        var riccardo = VoiceCatalog.Packages.Single(p => p.Id == "piper-riccardo");
        Assert.Equal(22_477_236 + 28_130_791 + 4_161, _tts.Catalog.DownloadSize(riccardo));
    }

    [Fact]
    public void Kokoro_style_tables_are_read_from_numpy_files()
    {
        // A 2 x 2 float32 array as NumPy writes it (format 1.0, header padded to 64 bytes).
        string header = "{'descr': '<f4', 'fortran_order': False, 'shape': (2, 2), }";
        header = header.PadRight(64 - 10 - 1) + "\n";
        var bytes = new List<byte> { 0x93 };
        bytes.AddRange("NUMPY"u8.ToArray());
        bytes.AddRange([1, 0, (byte)header.Length, 0]);
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(header));
        foreach (float f in new[] { 1f, 2f, 3f, 4f })
            bytes.AddRange(BitConverter.GetBytes(f));
        Assert.Equal([1f, 2f, 3f, 4f], KokoroModel.ReadNpyFloats([.. bytes]));
    }

    [Fact]
    public void Long_kokoro_texts_go_in_batches_that_fit_the_model()
    {
        string sentence = string.Join(' ', Enumerable.Repeat("trˈɛno", 20)) + ".";
        var sentences = Enumerable.Repeat(sentence, 10).ToList();
        var batches = KokoroModel.Batches(sentences);
        Assert.True(batches.Count > 1);
        Assert.All(batches, b => Assert.InRange(KokoroModel.Tokens(b).Length, 1, 510));
        // Nothing lost: only the spaces between batches go.
        Assert.Equal(KokoroModel.Tokens(string.Join(' ', sentences)).Length,
            batches.Sum(b => KokoroModel.Tokens(b).Length) + batches.Count - 1);
    }

    private static TimeSpan Duration(string path)
    {
        using var reader = new WaveFileReader(path);
        return reader.TotalTime;
    }

    public void Dispose()
    {
        _tts.Dispose();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
