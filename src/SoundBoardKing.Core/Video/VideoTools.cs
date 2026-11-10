using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace SoundBoardKing.Core.Video;

/// <summary>
/// The free programs the video import runs, downloaded the first time they are needed into the local
/// app data, never shipped with the app: FFmpeg (reads any video, cuts the audio out), yt-dlp (downloads
/// from YouTube, TikTok, Instagram, Facebook and many more) and Deno (yt-dlp needs it for YouTube).
/// FFmpeg and Deno are pinned versions checked against their SHA-256; yt-dlp has to follow the sites, which
/// change often, so it is always the latest release, checked against the list of hashes published with it,
/// and it updates itself.
/// </summary>
public sealed class VideoTools(string folder)
{
    // Gyan's "essentials" build: ffmpeg.exe and ffprobe.exe are all that is taken from it.
    internal const string FfmpegVersion = "9.0.2";
    private const string FfmpegZipName = "ffmpeg-" + FfmpegVersion + "-essentials_build.zip";
    private static readonly Uri FfmpegSource = new($"https://github.com/GyanD/codexffmpeg/releases/download/{FfmpegVersion}/{FfmpegZipName}");
    private const string FfmpegSha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";
    public const long FfmpegDownloadBytes = 114_768_076;

    internal const string DenoVersion = "2.9.8";
    private static readonly Uri DenoSource = new($"https://github.com/denoland/deno/releases/download/v{DenoVersion}/deno-x86_64-pc-windows-msvc.zip");
    private const string DenoSha256 = "cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf";
    private const long DenoDownloadBytes = 42_917_472;

    private static readonly Uri YtDlpSource = new("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe");
    private static readonly Uri YtDlpHashes = new("https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS");
    private const long YtDlpDownloadBytes = 24_000_000;

    /// <summary>About how much the first link downloads, for the question asked before it.</summary>
    public const long DownloaderDownloadBytes = YtDlpDownloadBytes + DenoDownloadBytes;

    // yt-dlp looks for an update at most this often.
    private static readonly TimeSpan UpdateEvery = TimeSpan.FromDays(1);

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundBoardKing", "Strumenti");

    public string Folder { get; } = folder;

    private string FfmpegFolder => Path.Combine(Folder, "ffmpeg-" + FfmpegVersion);
    private string DenoFolder => Path.Combine(Folder, "deno-" + DenoVersion);

    public string FfmpegPath => Path.Combine(FfmpegFolder, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(FfmpegFolder, "ffprobe.exe");
    public string YtDlpPath => Path.Combine(Folder, "yt-dlp", "yt-dlp.exe");
    public string DenoPath => Path.Combine(DenoFolder, "deno.exe");

    private string UpdateStampPath => Path.Combine(Folder, "yt-dlp", "ultimo-controllo.txt");

    public bool IsFfmpegInstalled => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    public bool IsDownloaderInstalled => File.Exists(YtDlpPath) && File.Exists(DenoPath);

    /// <summary>Downloads FFmpeg and keeps ffmpeg.exe and ffprobe.exe; <paramref name="progress"/> gets 0..1.</summary>
    /// <exception cref="HttpRequestException">No connection, or the file is gone.</exception>
    /// <exception cref="InvalidDataException">The download is not the expected file.</exception>
    public async Task InstallFfmpegAsync(HttpClient http, IProgress<double>? progress, CancellationToken cancel)
    {
        await InstallFromZipAsync(http, FfmpegSource, FfmpegSha256, FfmpegDownloadBytes, FfmpegFolder,
            ["ffmpeg.exe", "ffprobe.exe"], progress, cancel);
        DeleteOlder("ffmpeg-", FfmpegFolder);
    }

    /// <summary>Downloads yt-dlp and Deno; <paramref name="progress"/> gets 0..1 for the two together.</summary>
    /// <exception cref="HttpRequestException">No connection, or a file is gone.</exception>
    /// <exception cref="InvalidDataException">A download is not the expected file.</exception>
    public async Task InstallDownloaderAsync(HttpClient http, IProgress<double>? progress, CancellationToken cancel)
    {
        double ytShare = (double)YtDlpDownloadBytes / DownloaderDownloadBytes;
        if (!File.Exists(YtDlpPath))
        {
            var sums = await http.GetStringAsync(YtDlpHashes, cancel);
            var expected = HashOf(sums, "yt-dlp.exe")
                ?? throw new InvalidDataException("La lista delle impronte di yt-dlp non contiene il programma: riprova più tardi.");
            Directory.CreateDirectory(Path.GetDirectoryName(YtDlpPath)!);
            var partial = YtDlpPath + ".part";
            try
            {
                await DownloadAsync(http, YtDlpSource, partial, expected, YtDlpDownloadBytes,
                    Scale(progress, 0, ytShare), cancel);
                File.Move(partial, YtDlpPath, overwrite: true);
                File.WriteAllText(UpdateStampPath, DateTime.UtcNow.ToString("O"));
            }
            finally
            {
                TryDelete(partial);
            }
        }
        if (!File.Exists(DenoPath))
        {
            await InstallFromZipAsync(http, DenoSource, DenoSha256, DenoDownloadBytes, DenoFolder, ["deno.exe"],
                Scale(progress, ytShare, 1), cancel);
            DeleteOlder("deno-", DenoFolder);
        }
        progress?.Report(1);
    }

    /// <summary>
    /// Lets yt-dlp update itself when it has not looked for a while: the sites change and an old
    /// version stops working. A failure (no connection) is not an error, the old version is tried.
    /// </summary>
    public async Task UpdateDownloaderAsync(CancellationToken cancel)
    {
        if (!File.Exists(YtDlpPath) || DateTime.UtcNow - LastUpdateCheck() < UpdateEvery)
            return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var result = await ToolProcess.RunAsync(YtDlpPath, ["--update", "--no-config"], cancel: timeout.Token);
            if (result.Succeeded)
                File.WriteAllText(UpdateStampPath, DateTime.UtcNow.ToString("O"));
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private DateTime LastUpdateCheck()
    {
        try
        {
            return DateTime.TryParse(File.ReadAllText(UpdateStampPath), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? when
                : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>The hash of <paramref name="fileName"/> in a "hash  name" list such as SHA2-256SUMS.</summary>
    internal static string? HashOf(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName && parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private static async Task InstallFromZipAsync(HttpClient http, Uri source, string sha256, long size, string target,
        IReadOnlyList<string> keep, IProgress<double>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(target);
        var zip = Path.Combine(target, "download.zip.part");
        try
        {
            await DownloadAsync(http, source, zip, sha256, size, progress, cancel);
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zip);
                foreach (var name in keep)
                {
                    var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"Nel pacchetto scaricato manca {name}.");
                    var path = Path.Combine(target, name);
                    entry.ExtractToFile(path + ".part", overwrite: true);
                    File.Move(path + ".part", path, overwrite: true);
                }
            }, cancel);
        }
        finally
        {
            TryDelete(zip);
        }
    }

    private static async Task DownloadAsync(HttpClient http, Uri source, string path, string sha256, long expectedSize,
        IProgress<double>? progress, CancellationToken cancel)
    {
        using (var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? expectedSize;
            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                progress?.Report(Math.Min(1, (double)done / total));
            }
        }
        await using var check = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancel));
        if (hash != sha256)
            throw new InvalidDataException($"Il file scaricato da {source.Host} non è quello atteso: riprova più tardi.");
    }

    // Versions replaced by a newer pinned one.
    private void DeleteOlder(string prefix, string current)
    {
        try
        {
            foreach (var old in Directory.EnumerateDirectories(Folder, prefix + "*"))
            {
                if (!string.Equals(Path.GetFullPath(old), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(old, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IProgress<double>? Scale(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Progress<double>(p => progress.Report(from + (to - from) * p));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
