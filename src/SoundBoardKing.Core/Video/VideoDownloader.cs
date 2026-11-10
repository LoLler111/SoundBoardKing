using System.Globalization;

namespace SoundBoardKing.Core.Video;

/// <summary>
/// A video brought down from a site: one file with both, or a video file and an audio file (the app
/// probes them to tell which is which).
/// </summary>
public sealed record DownloadedVideo(string Title, IReadOnlyList<string> Files);

/// <summary>Browsers yt-dlp can borrow the login from, for Instagram and Facebook videos that need one.</summary>
public enum CookieBrowser
{
    None,
    Firefox,
    Edge,
    Chrome,
    Brave,
    Opera,
}

/// <summary>
/// Downloads a video from a link with yt-dlp: separately a video stream for the preview (H.264 up to
/// 720p when there is one, so Windows plays it without converting) and the best audio stream, which is
/// what becomes the sound. Sites that only give both together (TikTok) download once.
/// </summary>
public static class VideoDownloader
{
    /// <summary>Longest video accepted: past this the audio does not fit in memory comfortably.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(2);

    private const string TitleMark = "SBKTITLE ";
    private const string FileMark = "SBKFILE ";
    private const string ProgressMark = "SBKPROG ";

    /// <summary>Whether <paramref name="text"/> looks like a web link (yt-dlp then decides if it knows the site).</summary>
    public static bool IsLink(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.');

    /// <exception cref="VideoDownloadException">yt-dlp could not download it; the message says why, in Italian.</exception>
    public static async Task<DownloadedVideo> DownloadAsync(VideoTools tools, string link, string folder, CookieBrowser cookies,
        IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        var arguments = new List<string>
        {
            "--ignore-config", "--no-playlist", "--no-mtime", "--newline", "--no-colors", "--windows-filenames",
            // --print makes yt-dlp quiet: the progress has to be asked for.
            "--progress",
            "--encoding", "utf-8",
            "--ffmpeg-location", Path.GetDirectoryName(tools.FfmpegPath)!,
            // A video stream (720p at most, H.264 first) and, apart, the best audio; just the best whole file when a site has nothing apart.
            "-f", "bv*,ba/b",
            "-S", "res:720,vcodec:h264",
            // No live streams (they never end), nothing too long; "<=?" lets through a video whose length the site does not say.
            "--match-filters", $"!is_live & duration <=? {(int)MaxDuration.TotalSeconds}",
            "-P", folder,
            "-o", "%(id).40s.f%(format_id).30s.%(ext)s",
            "--no-simulate",
            "--print", "before_dl:" + TitleMark + "%(title)s",
            "--print", "after_move:" + FileMark + "%(filepath)s",
            "--progress-template", "download:" + ProgressMark + "%(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
        };
        if (File.Exists(tools.DenoPath))
            arguments.AddRange(["--js-runtimes", "deno:" + tools.DenoPath]);
        if (cookies != CookieBrowser.None)
            arguments.AddRange(["--cookies-from-browser", cookies.ToString().ToLowerInvariant()]);
        arguments.Add("--");
        arguments.Add(link.Trim());

        string? title = null;
        var files = new List<string>();
        int part = 0;
        double lastFraction = 0;
        var result = await ToolProcess.RunAsync(tools.YtDlpPath, arguments, onOutput: line =>
        {
            if (line.StartsWith(TitleMark, StringComparison.Ordinal))
                title ??= line[TitleMark.Length..].Trim();
            else if (line.StartsWith(FileMark, StringComparison.Ordinal))
                files.Add(line[FileMark.Length..].Trim());
            else if (ParseProgress(line) is { } fraction)
            {
                // Each stream starts again from 0.
                if (fraction < lastFraction - 0.5)
                    part++;
                lastFraction = fraction;
                progress?.Report(new DownloadProgress(part, fraction));
            }
        }, cancel: cancel);

        var existing = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!result.Succeeded || existing.Count == 0)
            throw new VideoDownloadException(ExplainError(result.Errors, existing.Count == 0 && result.Succeeded));

        return new DownloadedVideo(title ?? Path.GetFileNameWithoutExtension(existing[0]), existing);
    }

    /// <summary>The fraction of a "SBKPROG downloaded total estimate" line, or null.</summary>
    internal static double? ParseProgress(string line)
    {
        if (!line.StartsWith(ProgressMark, StringComparison.Ordinal))
            return null;
        var parts = line[ProgressMark.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !TryNumber(parts[0], out double done))
            return null;
        double total = TryNumber(parts[1], out double exact) && exact > 0 ? exact
            : TryNumber(parts[2], out double estimate) && estimate > 0 ? estimate : 0;
        return total > 0 ? Math.Clamp(done / total, 0, 1) : null;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>yt-dlp's last error, said plainly when it is a common one.</summary>
    internal static string ExplainError(IReadOnlyList<string> errors, bool skipped)
    {
        var error = errors.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? errors.LastOrDefault() ?? "";
        var lower = error.ToLowerInvariant();
        if (skipped || lower.Contains("does not pass filter"))
            return $"Il video dura più di {MaxDuration.TotalHours:0} ore, oppure è una diretta: scegline uno più corto, già concluso.";
        if (lower.Contains("unsupported url"))
            return "Questo link non porta a un video che so scaricare.";
        // Reading the browser's cookies failed (Chrome keeps them locked while it is open).
        if (lower.Contains("cookie") && (lower.Contains("could not") || lower.Contains("failed to") || lower.Contains("decrypt")))
            return "Non riesco a leggere l'accesso dal browser: chiudilo del tutto e riprova, oppure scegli un altro browser.";
        if (lower.Contains("private") || lower.Contains("login") || lower.Contains("log in") || lower.Contains("sign in") ||
            lower.Contains("cookies") || lower.Contains("age-restricted") || lower.Contains("confirm your age"))
            return "Il sito chiede di aver fatto l'accesso per questo video. Scegli sotto il browser in cui sei collegato e riprova." +
                (error.Length > 0 ? $"\n\n({Trim(error)})" : "");
        if (lower.Contains("not available in your country") || lower.Contains("geo restrict") || lower.Contains("geo-restrict"))
            return "Il video non è disponibile in Italia.";
        if (lower.Contains("removed") || lower.Contains("video unavailable") || lower.Contains("not found") || lower.Contains("404"))
            return "Il video non esiste più o non è disponibile.";
        if (lower.Contains("getaddrinfo") || lower.Contains("timed out") || lower.Contains("connection") || lower.Contains("unable to download webpage"))
            return "Il sito non risponde: controlla la connessione e riprova." + (error.Length > 0 ? $"\n\n({Trim(error)})" : "");
        return error.Length > 0
            ? $"Scaricamento non riuscito: {Trim(error)}"
            : "Scaricamento non riuscito.";
    }

    private static string Trim(string error)
    {
        error = error.StartsWith("ERROR:", StringComparison.Ordinal) ? error[6..].Trim() : error.Trim();
        return error.Length <= 300 ? error : error[..297] + "…";
    }
}

/// <summary>How far a download is: which stream (0 the video, 1 the audio) and how much of it.</summary>
public readonly record struct DownloadProgress(int Part, double Fraction);

public sealed class VideoDownloadException(string message) : Exception(message);
