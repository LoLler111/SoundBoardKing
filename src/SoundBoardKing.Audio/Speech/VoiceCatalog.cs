using System.IO.Compression;
using System.Security.Cryptography;

namespace SoundBoardKing.Audio.Speech;

/// <summary>One file of a download, checked against its known size and SHA-256.</summary>
internal sealed record CatalogFile(string Name, Uri Source, long Size, string Sha256);

/// <summary>One voice of a package: Piper models have one, Kokoro has a style per voice.</summary>
public sealed record CatalogSpeaker(string Id, string Name, string Gender);

/// <summary>Neural voices downloaded together: one Piper model, or the Kokoro model with its voices.</summary>
public sealed class CatalogPackage
{
    internal CatalogPackage(string id, string title, TtsEngine engine, string language, string espeakVoice, string credits,
        IReadOnlyList<CatalogSpeaker> speakers, IReadOnlyList<CatalogFile> files)
    {
        Id = id;
        Title = title;
        Engine = engine;
        Language = language;
        ESpeakVoice = espeakVoice;
        Credits = credits;
        Speakers = speakers;
        Files = files;
    }

    public string Id { get; }
    public string Title { get; }
    public TtsEngine Engine { get; }
    public string Language { get; }

    /// <summary>Who made the voices and under which license.</summary>
    public string Credits { get; }

    public IReadOnlyList<CatalogSpeaker> Speakers { get; }

    internal string ESpeakVoice { get; }
    internal IReadOnlyList<CatalogFile> Files { get; }

    public IEnumerable<TtsVoice> ToVoices() =>
        Speakers.Select(s => new TtsVoice(s.Name, Language, s.Gender, Engine, VoiceId(s)));

    internal string VoiceId(CatalogSpeaker speaker) => $"{Id}:{speaker.Id}";
}

/// <summary>
/// The neural voices the app can download, and where they go: the local app data (not next to the app,
/// so updates leave them). Every file is pinned to a release and checked.
/// </summary>
public sealed class VoiceCatalog(string folder)
{
    // eSpeak NG (GPL-3.0), as built by Piper for Windows: it turns text into phonemes for every neural voice.
    // Only espeak-ng.dll and its data are kept from the archive.
    private static readonly CatalogFile ESpeakArchive = new("piper_windows_amd64.zip",
        new("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip"),
        22_477_236, "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea");

    private const string PiperVoices = "https://huggingface.co/rhasspy/piper-voices/resolve/e7229f7d33a10e145e109b886d661984201ac3c4/";
    private const string KokoroFiles = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/";
    private const string Italian = "Italiano (Italia)";

    public static IReadOnlyList<CatalogPackage> Packages { get; } =
    [
        Piper("piper-riccardo", new("riccardo", "Riccardo (Piper)", "maschile"), "it/it_IT/riccardo/x_low/it_IT-riccardo-x_low",
            28_130_791, "1368de15f123275a7ef951c9e5e30be0f58a032daa14a0da44037443c1d1d21b",
            4_161, "146ab9c634afe524e9fb7530f2510df7a42fb1db56b52658ca1fb3d98001a62a",
            "Piper · dataset M-AILABS · qualità bassa (16 kHz)"),
        Piper("piper-paola", new("paola", "Paola (Piper)", "femminile"), "it/it_IT/paola/medium/it_IT-paola-medium",
            63_511_038, "6fc918b5a0ea6137382833dddfa567bffbe6a5060c02043c87192ee59c04210c",
            7_099, "aea19c0a7fce29fbc359b93f10e7902854401e4c95ae2ea328ae516b15d296cf",
            "Piper · dataset di Paola Persico (CC0)"),
        Piper("piper-serena", new("serena", "Serena (Piper)", "femminile"), "it/it_IT/serena/medium/it_IT-serena-medium",
            63_516_051, "fe4e26b2c1236e2a44d2e295cad564d94a0d8b0a9fca1ccdfd70dd4af3116eb5",
            5_254, "14cb4995874138b2792ae07753b5540942124761af9ae6609f9b520f28837881",
            "Piper · dataset serena-synthetic-it di committa (CC BY 4.0)"),
        new CatalogPackage("kokoro-it", "Kokoro: Nicola e Sara", TtsEngine.Kokoro, Italian, "it",
            "Kokoro-82M di hexgrad (Apache 2.0) · contiene le voci di tutte le lingue, qui quelle italiane",
            [new("im_nicola", "Nicola (Kokoro)", "maschile"), new("if_sara", "Sara (Kokoro)", "femminile")],
            [
                new("kokoro-v1.0.int8.onnx", new(KokoroFiles + "kokoro-v1.0.int8.onnx"),
                    92_361_271, "6e742170d309016e5891a994e1ce1559c702a2ccd0075e67ef7157974f6406cb"),
                new("voices-v1.0.bin", new(KokoroFiles + "voices-v1.0.bin"),
                    28_214_398, "bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d"),
            ]),
    ];

    private const string RvcFiles = "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/e6d0c1a17da07c33557852f9dfa2bd44cc75737d/";

    /// <summary>
    /// What every RVC voice needs besides its own model: ContentVec (HuBERT base, the weights for the
    /// graph shipped with the app) and RMVPE for the pitch, both from RVC's own release (MIT).
    /// </summary>
    public static CatalogPackage RvcSupport { get; } = new("rvc-support", "Componenti RVC", TtsEngine.Rvc, "", "",
        "ContentVec e RMVPE dal progetto RVC (lj1995/VoiceConversionWebUI, MIT)", [],
        [
            new("hubert_base.bin", new(RvcFiles + "hubert_base/pytorch_model.bin"),
                189_206_711, "cc8c20f4b90a520757260197a3ff2505705a7adbd20ad9eeaa4e1a9b38442ef5"),
            new("rmvpe.onnx", new(RvcFiles + "rmvpe.onnx"),
                361_688_443, "5370e71ac80af8b4b7c793d27efd51fd8bf962de3a7ede0766dac0befa3660fd"),
        ]);

    public string Folder { get; } = folder;

    internal string ESpeakFolder => Path.Combine(Folder, "espeak");

    internal string ContentVecPath => PathOf(RvcSupport, RvcSupport.Files[0]);
    internal string RmvpePath => PathOf(RvcSupport, RvcSupport.Files[1]);

    private bool IsESpeakInstalled => File.Exists(Path.Combine(ESpeakFolder, "espeak-ng.dll"))
                                      && File.Exists(Path.Combine(ESpeakFolder, "espeak-ng-data", "phontab"));

    // eSpeak turns text into phonemes: the RVC components do not need it.
    private bool NeedsESpeak(CatalogPackage package) => package.Engine != TtsEngine.Rvc && !IsESpeakInstalled;

    public bool IsInstalled(CatalogPackage package) =>
        !NeedsESpeak(package) && package.Files.All(f => IsComplete(PathOf(package, f)));

    /// <summary>Every voice of the installed packages.</summary>
    public IReadOnlyList<TtsVoice> InstalledVoices() =>
        Packages.Where(IsInstalled).SelectMany(p => p.ToVoices()).ToList();

    /// <summary>What is still to download for <paramref name="package"/>, in bytes.</summary>
    public long DownloadSize(CatalogPackage package) =>
        (NeedsESpeak(package) ? ESpeakArchive.Size : 0)
        + package.Files.Where(f => !IsComplete(PathOf(package, f))).Sum(f => f.Size);

    internal string PathOf(CatalogPackage package, CatalogFile file) => Path.Combine(Folder, package.Id, file.Name);

    /// <summary>The package and voice behind a <see cref="TtsVoice.Id"/> of a neural voice.</summary>
    internal static (CatalogPackage Package, CatalogSpeaker Speaker)? Find(string voiceId)
    {
        foreach (var package in Packages)
        {
            foreach (var speaker in package.Speakers)
            {
                if (package.VoiceId(speaker) == voiceId)
                    return (package, speaker);
            }
        }
        return null;
    }

    /// <summary>Downloads what <paramref name="package"/> still needs; <paramref name="progress"/> gets 0..1.</summary>
    /// <exception cref="HttpRequestException">No connection, or a file is gone.</exception>
    /// <exception cref="InvalidDataException">A download is not the expected file.</exception>
    public async Task DownloadAsync(CatalogPackage package, HttpClient http, IProgress<double>? progress, CancellationToken cancel)
    {
        var missing = package.Files.Where(f => !IsComplete(PathOf(package, f))).ToList();
        double total = Math.Max(1, DownloadSize(package));
        long done = 0;
        var step = new SyncProgress(bytes => progress?.Report(Math.Min(1, (done + bytes) / total)));

        if (NeedsESpeak(package))
        {
            string archive = Path.Combine(Folder, ESpeakArchive.Name);
            await DownloadFileAsync(http, ESpeakArchive, archive, step, cancel);
            try
            {
                await Task.Run(() => ExtractESpeak(archive), cancel);
            }
            finally
            {
                File.Delete(archive);
            }
            done += ESpeakArchive.Size;
        }
        foreach (var file in missing)
        {
            await DownloadFileAsync(http, file, PathOf(package, file), step, cancel);
            done += file.Size;
        }
        progress?.Report(1);
    }

    /// <summary>Removes the package's files. eSpeak stays: it is small and every neural voice needs it.</summary>
    public void Delete(CatalogPackage package)
    {
        string folder = Path.Combine(Folder, package.Id);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    private void ExtractESpeak(string archive)
    {
        string staging = ESpeakFolder + ".new";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(archive))
        {
            foreach (var entry in zip.Entries)
            {
                // Inside the archive everything is under "piper/".
                string name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith("piper/", StringComparison.Ordinal))
                    continue;
                name = name["piper/".Length..];
                if (name != "espeak-ng.dll" && !name.StartsWith("espeak-ng-data/", StringComparison.Ordinal))
                    continue;
                string target = Path.GetFullPath(Path.Combine(staging, name));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        if (Directory.Exists(ESpeakFolder))
            Directory.Delete(ESpeakFolder, recursive: true);
        Directory.Move(staging, ESpeakFolder);
    }

    private static bool IsComplete(string path) => File.Exists(path);

    private static async Task DownloadFileAsync(HttpClient http, CatalogFile file, string path, IProgress<long> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string partial = path + ".part";
        try
        {
            using (var response = await http.GetAsync(file.Source, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    progress.Report(done);
                }
            }

            await using (var check = File.OpenRead(partial))
            {
                if (check.Length != file.Size
                    || Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancel)) != file.Sha256)
                    throw new InvalidDataException($"Il file scaricato {file.Name} non è quello atteso: riprova più tardi.");
            }
            // Moved in place only once checked: a file that exists is complete.
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    private static CatalogPackage Piper(string id, CatalogSpeaker speaker, string path,
        long modelSize, string modelSha, long configSize, string configSha, string credits)
    {
        string file = path[(path.LastIndexOf('/') + 1)..];
        return new CatalogPackage(id, speaker.Name, TtsEngine.Piper, Italian, "it", credits, [speaker],
        [
            new CatalogFile(file + ".onnx", new(PiperVoices + path + ".onnx"), modelSize, modelSha),
            new CatalogFile(file + ".onnx.json", new(PiperVoices + path + ".onnx.json"), configSize, configSha),
        ]);
    }

    // Reports on the downloading thread, so the running total is read before the next file adds to it.
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
