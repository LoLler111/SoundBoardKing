using NAudio.Wave;
using SoundBoardKing.Audio.Speech;
using SoundBoardKing.Audio.Speech.Rvc;

namespace SoundBoardKing.Audio.Tests;

/// <summary>
/// RVC needs a model (the user's) and 550 MB of support files: these tests run only where SBK_RVC_DIR names a
/// folder with roberto-model\ (the .pth and .index), rvc-support\ (pytorch_model.bin, rmvpe.onnx) and
/// rvc-ref\ (the arrays the Python reference pipeline wrote for rvc-in\nicola.wav).
/// </summary>
public class RvcTests
{
    private static readonly string? Root = Environment.GetEnvironmentVariable("SBK_RVC_DIR");

    private static string Model => Path.Combine(Root!, "roberto-model", "roberto.pth");
    private static string Index => Path.Combine(Root!, "roberto-model", "added_IVF4271_Flat_nprobe_1_roberto_v2.index");
    private static float[] Reference(string name) => KokoroModel.ReadNpyFloats(File.ReadAllBytes(Path.Combine(Root!, "rvc-ref", name + ".npy")));

    private static RvcConverter Converter() => new(
        Path.Combine(Root!, "rvc-support", "pytorch_model.bin"), Path.Combine(Root!, "rvc-support", "rmvpe.onnx"), Model, Index,
        Path.Combine(Root!, "rvc-cache"));

    [Fact]
    public void A_checkpoint_with_code_in_it_is_refused()
    {
        // A pickle that would call os.system: GLOBAL "os system", then REDUCE.
        string path = Path.Combine(Path.GetTempPath(), $"sbk-evil-{Guid.NewGuid():N}.pth");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("evil/data.pkl").Open();
            stream.Write([0x80, 0x02, (byte)'c', .. "os\nsystem\n"u8, (byte)'X', 4, 0, 0, 0, .. "echo"u8, 0x85, (byte)'R', (byte)'.']);
        }
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => TorchCheckpoint.Load(path));
            Assert.Contains("os system", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Intonation_widens_or_flattens_the_melody_around_its_average()
    {
        // Padding frames (the first and last two) are not counted in the average; unvoiced frames stay 0.
        float[] f0 = [400, 400, 100, 0, 200, 400, 25, 25];
        RvcConverter.ScaleIntonation(f0, 0.5f, 2);
        // Average of 100, 200, 400 in log Hz is 200: an octave away becomes half an octave.
        Assert.Equal(200 / MathF.Sqrt(2), f0[2], 0.01f);
        Assert.Equal(0, f0[3]);
        Assert.Equal(200, f0[4], 0.01f);
        Assert.Equal(200 * MathF.Sqrt(2), f0[5], 0.01f);

        float[] flat = [100, 200, 400];
        RvcConverter.ScaleIntonation(flat, 0, 0);
        Assert.All(flat, f => Assert.Equal(200, f, 0.01f));

        float[] same = [100, 200, 400];
        RvcConverter.ScaleIntonation(same, 1, 0);
        Assert.Equal([100f, 200f, 400f], same);
    }

    [Fact]
    public void The_users_model_reads_as_plain_weights()
    {
        if (Root is null)
            return;
        var model = (Dictionary<object, object?>)TorchCheckpoint.Load(Model)!;
        Assert.Equal("v2", model["version"]);
        Assert.Equal("48k", model["sr"]);
        var weights = TorchCheckpoint.Tensors(model["weight"]);
        Assert.Equal(457, weights.Count);
        Assert.Equal([109L, 256L], weights["emb_g.weight"].Shape);
        Assert.Equal(28_716_994, weights.Values.Sum(t => t.Data.LongLength));
    }

    [Fact]
    public void The_index_reads_as_faiss_does()
    {
        if (Root is null)
            return;
        using var index = new FaissIndex(Index);
        Assert.Equal(768, index.Dimension);
        Assert.Equal(166_601, index.Count);
    }

    [Fact]
    public void Each_step_matches_the_python_pipeline()
    {
        if (Root is null)
            return;
        float[] audio = Reference("audio16k");

        double[] filtered = RvcConverter.FiltFilt(audio);
        float[] expectedFiltered = Reference("filtered");
        Assert.True(filtered.Zip(expectedFiltered).Max(p => Math.Abs(p.First - p.Second)) < 1e-4, "filtro");

        var padded = new float[filtered.Length + 32000];
        for (int i = 0; i < padded.Length; i++)
            padded[i] = (float)filtered[RvcConverter.Reflect(i - 16000, filtered.Length)];

        using var converter = Converter();
        float[] f0 = converter.Pitch(padded);
        // Against Python running the same rmvpe.onnx (the PyTorch rmvpe.pt itself agrees with it on 94% of frames).
        float[] expectedF0 = Reference("f0_onnx");
        Assert.Equal(expectedF0.Length, f0.Length);
        int agree = 0, voiced = 0;
        var cents = new List<double>();
        for (int i = 0; i < f0.Length; i++)
        {
            if ((f0[i] > 0) == (expectedF0[i] > 0))
                agree++;
            if (f0[i] > 0 && expectedF0[i] > 0)
            {
                voiced++;
                cents.Add(Math.Abs(1200 * Math.Log2(f0[i] / expectedF0[i])));
            }
        }
        cents.Sort();
        Assert.True(agree > f0.Length * 0.99, $"voce/silenzio uguali in {agree} fotogrammi su {f0.Length}");
        Assert.True(cents[cents.Count / 2] < 2, $"differenza mediana {cents[cents.Count / 2]:0.0} centesimi");

        float[] content = converter.Content(padded, out int frames);
        float[] expectedContent = Reference("feats");
        Assert.Equal(expectedContent.Length, content.Length);
        double meanDiff = content.Zip(expectedContent).Average(p => Math.Abs(p.First - p.Second));
        Assert.True(meanDiff < 0.01, $"ContentVec, differenza media {meanDiff}");

        // The blend starts from the reference features, so only the index search is compared.
        float[] blended = (float[])expectedContent.Clone();
        converter.Index!.Blend(blended, frames, 0.66f);
        float[] expectedBlend = Reference("blended");
        int close = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            double diff = 0;
            for (int j = 0; j < 768; j++)
                diff = Math.Max(diff, Math.Abs(blended[frame * 768 + j] - expectedBlend[frame * 768 + j]));
            if (diff < 1e-3)
                close++;
        }
        Assert.True(close > frames * 0.98, $"indice: {close} fotogrammi uguali su {frames}");
    }

    [Fact]
    public async Task An_rvc_voice_speaks_text_through_its_base_voice()
    {
        // The neural voices folder (SBK_TTS_VOICES) with Kokoro and the RVC components in it.
        string? voices = Environment.GetEnvironmentVariable("SBK_TTS_VOICES");
        if (Root is null || voices is null)
            return;
        using var tts = new TextToSpeech(voices)
        {
            RvcVoices = [new RvcVoiceDefinition("Roberto (RVC)", Model, Index, "Nicola (Kokoro)", Pitch: -3)],
        };
        Assert.True(tts.IsRvcReady);
        var roberto = Assert.Single(await tts.GetVoicesAsync(), v => v.Engine == TtsEngine.Rvc);
        Assert.Equal("Italiano (Italia)", roberto.Language);

        string folder = Path.Combine(Root, "rvc-out-cs");
        string path = await tts.SynthesizeAsync("Il treno regionale per Venezia Santa Lucia è in partenza dal binario 7.", roberto, -2, folder);
        using (var reader = new WaveFileReader(path))
        {
            Assert.Equal(48000, reader.WaveFormat.SampleRate);
            Assert.True(reader.TotalTime > TimeSpan.FromSeconds(2), reader.TotalTime.ToString());
        }
        File.Copy(path, Path.Combine(folder, "roberto_dal_testo.wav"), overwrite: true);
    }

    [Fact]
    public void Nicola_becomes_roberto_through_the_whole_pipeline()
    {
        if (Root is null)
            return;
        float[] audio = Reference("audio16k");
        using var converter = Converter();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        float[] output = converter.Convert(audio, new RvcSettings(Pitch: -3), CancellationToken.None);
        watch.Stop();
        double seconds = output.Length / (double)RvcConverter.OutputRate;
        Assert.InRange(seconds, audio.Length / 16000.0 - 0.1, audio.Length / 16000.0 + 0.1);
        Assert.True(output.Max(Math.Abs) > 0.05, "silenzio");

        // Kept for listening to.
        string folder = Path.Combine(Root, "rvc-out-cs");
        Directory.CreateDirectory(folder);
        using var writer = new WaveFileWriter(Path.Combine(folder, "roberto_da_nicola_-3.wav"), new WaveFormat(48000, 16, 1));
        writer.WriteSamples(output, 0, output.Length);
        Console.WriteLine($"conversione: {watch.Elapsed.TotalSeconds:0.0} s per {seconds:0.0} s di voce");
    }
}
