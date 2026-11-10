using SoundBoardKing.Core.Video;

namespace SoundBoardKing.Core.Tests;

public class VideoImportTests
{
    // What FFprobe prints for a YouTube download with both streams, trimmed.
    private const string VideoProbe = """
        {
          "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1280, "height": 720, "duration": "212.045000",
              "disposition": { "default": 1, "attached_pic": 0 } },
            { "index": 1, "codec_name": "aac", "codec_type": "audio", "sample_rate": "44100", "channels": 2, "duration": "212.091000",
              "disposition": { "default": 1, "attached_pic": 0 } }
          ],
          "format": { "filename": "x.mp4", "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "212.091000" }
        }
        """;

    [Fact]
    public void Probe_reads_streams_and_duration()
    {
        var info = MediaProbe.Parse(VideoProbe);

        Assert.Equal(TimeSpan.FromSeconds(212.091), info.Duration);
        Assert.Equal(44100, info.AudioSampleRate);
        Assert.Equal(2, info.AudioChannels);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal((1280, 720), (info.Width, info.Height));
        Assert.True(info.HasAudio);
        Assert.True(info.IsPlayableByWindows);
    }

    [Fact]
    public void Probe_ignores_cover_art_and_flags_webm_for_conversion()
    {
        var mp3 = MediaProbe.Parse("""
            { "streams": [
                { "codec_name": "mp3", "codec_type": "audio", "sample_rate": "48000", "channels": 2 },
                { "codec_name": "mjpeg", "codec_type": "video", "width": 600, "height": 600, "disposition": { "attached_pic": 1 } } ],
              "format": { "format_name": "mp3", "duration": "3.5" } }
            """);
        Assert.False(mp3.HasVideo);
        Assert.True(mp3.HasAudio);

        var webm = MediaProbe.Parse("""
            { "streams": [ { "codec_name": "vp9", "codec_type": "video", "width": 640, "height": 360 },
                           { "codec_name": "opus", "codec_type": "audio", "sample_rate": "48000", "channels": 2 } ],
              "format": { "format_name": "matroska,webm", "duration": "10" } }
            """);
        Assert.True(webm.HasVideo);
        Assert.False(webm.IsPlayableByWindows);
    }

    [Fact]
    public void Probe_without_audio_or_video_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => MediaProbe.Parse("""{ "streams": [], "format": { "format_name": "tty" } }"""));
    }

    [Theory]
    [InlineData("SBKPROG 512 1024 NA", 0.5)]
    [InlineData("SBKPROG 300 NA 1200.0", 0.25)]
    [InlineData("SBKPROG 2048 1024 NA", 1.0)]
    public void Download_progress_is_read(string line, double expected)
    {
        Assert.Equal(expected, VideoDownloader.ParseProgress(line)!.Value, 3);
    }

    [Theory]
    [InlineData("SBKPROG 512 NA NA")]
    [InlineData("[download] 50% of 10MiB")]
    [InlineData("SBKTITLE 512 1024 0")]
    public void Lines_without_a_known_size_give_no_progress(string line)
    {
        Assert.Null(VideoDownloader.ParseProgress(line));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("  https://vm.tiktok.com/ZMabc/  ", true)]
    [InlineData("http://instagram.com/reel/xyz", true)]
    [InlineData("ftp://example.com/video.mp4", false)]
    [InlineData("youtube.com/watch?v=x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Links_are_recognized(string? text, bool expected)
    {
        Assert.Equal(expected, VideoDownloader.IsLink(text));
    }

    [Fact]
    public void Common_download_errors_are_explained_in_italian()
    {
        Assert.Contains("accesso", VideoDownloader.ExplainError(
            ["ERROR: [Instagram] C1x: Requested content is not available, rate-limit reached or login required. Use --cookies"], false));
        Assert.Contains("non porta a un video", VideoDownloader.ExplainError(["ERROR: Unsupported URL: https://example.com/"], false));
        Assert.Contains("2 ore", VideoDownloader.ExplainError([], skipped: true));
        Assert.Contains("chiudilo", VideoDownloader.ExplainError(["ERROR: Could not copy Chrome cookie database."], false));
        Assert.Contains("non esiste più", VideoDownloader.ExplainError(["ERROR: [youtube] abc: Video unavailable. This video has been removed"], false));
        // Anything else is shown as yt-dlp says it, without its prefix.
        Assert.Equal("Scaricamento non riuscito: something odd", VideoDownloader.ExplainError(["WARNING: x", "ERROR: something odd"], false));
    }

    [Fact]
    public void Hash_list_gives_the_hash_of_the_file()
    {
        const string sums = """
            1111111111111111111111111111111111111111111111111111111111111111  yt-dlp
            F5389D3698ECDE4EF06F4E6B1CE0294FE9AC1396228C8990B8D3416D6D177A7D  yt-dlp.exe
            2222222222222222222222222222222222222222222222222222222222222222  yt-dlp_x86.exe
            """;
        Assert.Equal("f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d", VideoTools.HashOf(sums, "yt-dlp.exe"));
        Assert.Null(VideoTools.HashOf(sums, "yt-dlp_macos"));
    }

    [Theory]
    [InlineData("out_time_us=12500000", 12.5)]
    [InlineData("out_time_us=0", 0.0)]
    public void Ffmpeg_progress_is_read(string line, double seconds)
    {
        Assert.Equal(seconds, FfmpegMedia.ProgressSeconds(line));
    }

    [Theory]
    [InlineData("out_time_us=N/A")]
    [InlineData("out_time_ms=12500000x")]
    [InlineData("speed=1.5x")]
    public void Other_ffmpeg_lines_give_no_progress(string line)
    {
        Assert.Null(FfmpegMedia.ProgressSeconds(line));
    }

    [Fact]
    public void Short_videos_are_held_at_full_quality()
    {
        var format = PreviewFormat.Choose(TimeSpan.FromMinutes(4), 44100, 6);

        Assert.Equal(new PreviewFormat(44100, 2, IsFullQuality: true), format);
    }

    [Fact]
    public void Long_videos_are_held_smaller_and_read_again_when_saved()
    {
        var film = PreviewFormat.Choose(TimeSpan.FromHours(2), 48000, 2);
        var hiRes = PreviewFormat.Choose(TimeSpan.FromMinutes(3), 96000, 2);

        Assert.False(film.IsFullQuality);
        Assert.True(TimeSpan.FromHours(2).TotalSeconds * film.SampleRate * film.Channels <= PreviewFormat.SampleBudget);
        Assert.False(hiRes.IsFullQuality);
        Assert.Equal(48000, PreviewFormat.SaveRateFor(96000));
        Assert.Equal(44100, PreviewFormat.SaveRateFor(44100));
    }
}

public class ToolProcessTests
{
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task Output_and_errors_come_line_by_line_with_the_exit_code()
    {
        var seen = new List<string>();
        var result = await ToolProcess.RunAsync(Cmd, ["/c", "echo uno& echo due& echo male 1>&2& exit 3"], onOutput: seen.Add);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal(["uno", "due"], result.Output.Select(l => l.Trim()));
        Assert.Equal(["uno", "due"], seen.Select(l => l.Trim()));
        Assert.Equal("male", result.Errors.Single().Trim());
    }

    [Fact]
    public async Task Cancelling_stops_the_program()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ToolProcess.RunAsync(Cmd, ["/c", "ping -n 30 127.0.0.1 >nul"], cancel: cancel.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Binary_output_is_handed_over_whole()
    {
        byte[]? bytes = null;
        var result = await ToolProcess.RunBinaryAsync(Cmd, ["/c", "echo abc"], async (stream, token) =>
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, token);
            bytes = memory.ToArray();
        });

        Assert.True(result.Succeeded);
        Assert.Equal("abc\r\n"u8.ToArray(), bytes);
    }
}
