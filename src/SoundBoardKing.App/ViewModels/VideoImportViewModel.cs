using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.App.Services;
using SoundBoardKing.Audio;
using SoundBoardKing.Audio.Editing;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Video;

namespace SoundBoardKing.App.ViewModels;

public sealed record CategoryChoice(Category? Category, string Label);

public sealed record CookieBrowserChoice(CookieBrowser Browser, string Label);

/// <summary>
/// Takes a sound from a video: a file, or a link yt-dlp can download. The video is only there to see
/// where things happen; the waveform below is where the piece to keep is chosen, listened to, and
/// added to the library as an audio file.
/// </summary>
public sealed partial class VideoImportViewModel : ObservableObject, IDisposable
{
    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SoundBoardKing/{UpdateService.CurrentVersion}");
        return http;
    });

    private static string WorkRoot => Path.Combine(Path.GetTempPath(), "SoundBoardKing", "Video");

    private readonly MainViewModel _main;
    private readonly IDialogService _dialogs;
    private readonly PreviewPlayer _preview = new();
    private readonly DispatcherTimer _playheadTimer;
    private readonly string _workFolder;
    private CancellationTokenSource? _busyCancel;
    private int _playFrom;

    // The file the audio comes from, what is in it, and how it is held in memory.
    private string? _audioPath;
    private MediaInfo? _audioInfo;
    private PreviewFormat _format;
    // The video file as it came, to convert it when Windows turns out not to play it.
    private string? _originalVideoPath;
    private MediaInfo? _videoInfo;
    private int _loadGeneration;

    [ObservableProperty]
    private string link = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(HasSource), nameof(DurationText))]
    private AudioClip? clip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideo))]
    private string? videoPath;

    [ObservableProperty]
    private string sourceTitle = "";

    [ObservableProperty]
    private int selectionStart;

    [ObservableProperty]
    private int selectionEnd;

    [ObservableProperty]
    private double playhead = -1;

    [ObservableProperty]
    private bool isPreviewing;

    /// <summary>The time the video shows while nothing plays: the edge of the selection moved last.</summary>
    [ObservableProperty]
    private TimeSpan videoTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsIdle))]
    private bool isBusy;

    [ObservableProperty]
    private string busyText = "";

    [ObservableProperty]
    private double busyProgress;

    [ObservableProperty]
    private bool isBusyIndeterminate;

    [ObservableProperty]
    private string statusText = "Incolla il link di un video (YouTube, TikTok, Instagram, Facebook…) oppure apri un file video dal PC.";

    [ObservableProperty]
    private string newTitle = "";

    [ObservableProperty]
    private CategoryChoice selectedCategory;

    [ObservableProperty]
    private IReadOnlyList<ExportFormatInfo> formats = AudioExport.Formats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLossy))]
    private ExportFormatInfo selectedFormat = AudioExport.Formats[0];

    [ObservableProperty]
    private int selectedBitrate = AudioExport.DefaultBitrate;

    [ObservableProperty]
    private CookieBrowserChoice selectedCookieBrowser;

    public VideoImportViewModel(MainViewModel main, IDialogService dialogs)
    {
        _main = main;
        _dialogs = dialogs;
        _workFolder = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        DeleteOldWork();
        _preview.Volume = main.PreviewVolume;
        _preview.Ended += (_, _) => StopPlayhead();
        _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();

        Categories = [new CategoryChoice(null, "Nessuna (solo nella libreria)"),
            .. main.CategoriesInOrder().Select(c => new CategoryChoice(c.Category, new string(' ', c.Depth * 4) + c.Category.Name))];
        selectedCategory = Categories.FirstOrDefault(c => c.Category is not null && c.Category == main.SelectedCategoryModel) ?? Categories[0];

        CookieBrowsers =
        [
            new(CookieBrowser.None, "Nessuno"),
            new(CookieBrowser.Firefox, "Firefox"),
            new(CookieBrowser.Edge, "Edge"),
            new(CookieBrowser.Chrome, "Chrome"),
            new(CookieBrowser.Brave, "Brave"),
            new(CookieBrowser.Opera, "Opera"),
        ];
        selectedCookieBrowser = CookieBrowsers.FirstOrDefault(b => b.Browser.ToString() == main.Local.VideoCookieBrowser) ?? CookieBrowsers[0];
    }

    public IReadOnlyList<CategoryChoice> Categories { get; }
    public IReadOnlyList<CookieBrowserChoice> CookieBrowsers { get; }
    public IReadOnlyList<int> Bitrates => AudioExport.Bitrates;

    public bool HasSource => Clip is not null;
    public bool HasVideo => VideoPath is not null;
    public bool IsReady => Clip is not null && !IsBusy;
    public bool IsIdle => !IsBusy;
    public bool IsLossy => !SelectedFormat.IsLossless;
    public bool HasSelection => SelectionEnd > SelectionStart;

    public string DurationText => Clip is null ? "" : FormatTime(Clip.Duration);

    /// <summary>Volume of the preview, 0..100; shared with Myinstants and the editor.</summary>
    public double PreviewVolume
    {
        get => Math.Round(_main.PreviewVolume * 100);
        set
        {
            float volume = (float)Math.Clamp(value / 100, 0, 1);
            _main.PreviewVolume = volume;
            _preview.Volume = volume;
            OnPropertyChanged();
        }
    }

    partial void OnSelectedCookieBrowserChanged(CookieBrowserChoice value)
    {
        _main.Local.VideoCookieBrowser = value.Browser == CookieBrowser.None ? null : value.Browser.ToString();
        _main.SaveLocalSettings();
    }

    // ---- Selection ------------------------------------------------------------------------------

    public string SelectionStartText
    {
        get => FormatFrames(SelectionStart);
        set
        {
            if (ParseFrames(value) is int frame)
                SetSelection(frame, Math.Max(frame, SelectionEnd));
            OnPropertyChanged();
        }
    }

    public string SelectionEndText
    {
        get => FormatFrames(SelectionEnd);
        set
        {
            if (ParseFrames(value) is int frame)
                SetSelection(Math.Min(SelectionStart, frame), frame);
            OnPropertyChanged();
        }
    }

    public string SelectionLengthText
    {
        get => FormatFrames(SelectionEnd - SelectionStart);
        set
        {
            if (ParseFrames(value) is int frames)
                SetSelection(SelectionStart, SelectionStart + frames);
            OnPropertyChanged();
        }
    }

    // The video follows the edge being moved, so dragging the end of the selection shows where it ends.
    private bool _endMovedLast;

    partial void OnSelectionStartChanged(int value)
    {
        _endMovedLast = false;
        SelectionChanged();
    }

    partial void OnSelectionEndChanged(int value)
    {
        _endMovedLast = value != SelectionStart;
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionStartText));
        OnPropertyChanged(nameof(SelectionEndText));
        OnPropertyChanged(nameof(SelectionLengthText));
        AddCommand.NotifyCanExecuteChanged();
        AddAndEditCommand.NotifyCanExecuteChanged();
        if (Clip is { } clip && !IsPreviewing)
            VideoTime = clip.FramesToTime(_endMovedLast ? SelectionEnd : SelectionStart);
    }

    private void SetSelection(int start, int end)
    {
        int frames = Clip?.Frames ?? 0;
        start = Math.Clamp(start, 0, frames);
        end = Math.Clamp(end, 0, frames);
        if (end < start)
            (start, end) = (end, start);
        SelectionStart = start;
        SelectionEnd = end;
    }

    /// <summary>Moves the edge moved last (or the cursor) by <paramref name="seconds"/>, to find the exact frame with the arrows.</summary>
    public void Nudge(double seconds)
    {
        if (Clip is not { } clip)
            return;
        int delta = (int)Math.Round(seconds * clip.SampleRate);
        if (!HasSelection)
        {
            int at = Math.Clamp(SelectionStart + delta, 0, clip.Frames);
            SelectionStart = SelectionEnd = at;
        }
        else if (_endMovedLast)
        {
            SelectionEnd = Math.Clamp(SelectionEnd + delta, SelectionStart, clip.Frames);
        }
        else
        {
            SelectionStart = Math.Clamp(SelectionStart + delta, 0, SelectionEnd);
        }
    }

    // ---- Opening a video ------------------------------------------------------------------------

    [RelayCommand]
    private async Task LoadLinkAsync()
    {
        var link = Link.Trim();
        if (IsBusy)
            return;
        if (!VideoDownloader.IsLink(link))
        {
            StatusText = "Incolla un link che inizia con https://, ad esempio quello di un video di YouTube o TikTok.";
            return;
        }
        if (!await EnsureToolsAsync(downloader: true))
            return;

        int generation = BeginLoad();
        StartBusy("Controllo che yt-dlp sia aggiornato…", indeterminate: true);
        var cancel = _busyCancel!.Token;
        try
        {
            await _main.VideoTools.UpdateDownloaderAsync(cancel);
            BusyText = "Scarico il video…";
            IsBusyIndeterminate = false;
            var folder = Path.Combine(_workFolder, generation.ToString(CultureInfo.InvariantCulture));
            var progress = new Progress<DownloadProgress>(p =>
            {
                BusyText = p.Part == 0 ? "Scarico il video…" : "Scarico l'audio…";
                BusyProgress = p.Fraction * 100;
            });
            var video = await VideoDownloader.DownloadAsync(_main.VideoTools, link, folder, SelectedCookieBrowser.Browser, progress, cancel);

            BusyText = "Guardo cosa c'è nel video…";
            IsBusyIndeterminate = true;
            var probed = new List<(string Path, MediaInfo Info)>();
            foreach (var file in video.Files)
                probed.Add((file, await MediaProbe.ProbeAsync(_main.VideoTools, file, cancel)));
            // The audio-only file when there is one (it is the best sound), else the one with both.
            var audio = probed.Where(p => p.Info.HasAudio).OrderBy(p => p.Info.HasVideo).FirstOrDefault();
            var picture = probed.FirstOrDefault(p => p.Info.HasVideo);
            if (audio.Path is null)
            {
                StatusText = "In questo video non c'è audio.";
                return;
            }
            await LoadSourceAsync(generation, video.Title, audio.Path, audio.Info, picture.Path, picture.Info, cancel);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Annullato.";
        }
        catch (VideoDownloadException ex)
        {
            StatusText = "Scaricamento non riuscito.";
            _dialogs.ShowError(ex.Message);
        }
        catch (Exception ex) when (IsToolFailure(ex))
        {
            StatusText = $"Impossibile aprire il video: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var file = _dialogs.OpenVideoFile();
        if (file is not null)
            await LoadFileAsync(file);
    }

    /// <summary>Opens a video (or audio) file from the PC, also when dropped on the window.</summary>
    public async Task LoadFileAsync(string file)
    {
        if (IsBusy || !File.Exists(file))
            return;
        if (!await EnsureToolsAsync(downloader: false))
            return;

        int generation = BeginLoad();
        StartBusy("Guardo cosa c'è nel file…", indeterminate: true);
        var cancel = _busyCancel!.Token;
        try
        {
            var info = await MediaProbe.ProbeAsync(_main.VideoTools, file, cancel);
            if (!info.HasAudio)
            {
                StatusText = "In questo file non c'è audio.";
                return;
            }
            await LoadSourceAsync(generation, Path.GetFileNameWithoutExtension(file), file, info,
                info.HasVideo ? file : null, info.HasVideo ? info : null, cancel);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Annullato.";
        }
        catch (Exception ex) when (IsToolFailure(ex))
        {
            StatusText = $"Impossibile aprire \"{Path.GetFileName(file)}\": {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    private int BeginLoad()
    {
        StopPreview();
        return ++_loadGeneration;
    }

    private async Task LoadSourceAsync(int generation, string title, string audioPath, MediaInfo audioInfo,
        string? videoFile, MediaInfo? videoInfo, CancellationToken cancel)
    {
        var duration = audioInfo.Duration > TimeSpan.Zero ? audioInfo.Duration : videoInfo?.Duration ?? TimeSpan.Zero;
        if (duration > VideoDownloader.MaxDuration)
        {
            StatusText = $"Il video dura più di {VideoDownloader.MaxDuration.TotalHours:0} ore: scegline uno più corto.";
            return;
        }

        var format = PreviewFormat.Choose(duration, audioInfo.AudioSampleRate, audioInfo.AudioChannels);
        BusyText = "Leggo l'audio…";
        BusyProgress = 0;
        IsBusyIndeterminate = false;
        var progress = new Progress<double>(p => BusyProgress = p * 100);
        var samples = await FfmpegMedia.ReadAudioAsync(_main.VideoTools, audioPath, null, null,
            format.SampleRate, format.Channels, duration, progress, cancel);
        if (samples.Length < format.Channels * format.SampleRate / 20)
        {
            StatusText = "L'audio di questo video è vuoto.";
            return;
        }
        var newFormats = Formats.Count == AudioExport.Formats.Count ? await Task.Run(AudioExport.AvailableFormats, cancel) : Formats;

        string? shown = null;
        if (videoFile is not null && videoInfo is not null)
            shown = videoInfo.IsPlayableByWindows ? videoFile : await MakePlayableAsync(videoFile, videoInfo, cancel);
        if (generation != _loadGeneration)
            return;

        _audioPath = audioPath;
        _audioInfo = audioInfo;
        _format = format;
        _originalVideoPath = videoFile;
        _videoInfo = videoInfo;
        Formats = newFormats;
        if (!Formats.Contains(SelectedFormat))
            SelectedFormat = Formats[0];
        SourceTitle = title;
        NewTitle = title.Length > MainViewModel.MaxTitleLength ? title[..MainViewModel.MaxTitleLength] : title;
        VideoPath = shown;
        Clip = new AudioClip(samples, format.SampleRate, format.Channels);
        SelectionStart = SelectionEnd = 0;
        VideoTime = TimeSpan.Zero;
        StatusText = (videoFile is null ? "Questo file ha solo l'audio. " : videoInfo is not null && shown is null ? "Il video non si riesce a mostrare, ma l'audio sì. " : "") +
            "Trascina sulla forma d'onda per scegliere il pezzo: Spazio ascolta, le frecce spostano di un decimo di secondo (Maiusc: un secondo).";
    }

    /// <summary>A copy of the video Windows can play, for the preview; null when FFmpeg cannot make it.</summary>
    private async Task<string?> MakePlayableAsync(string videoFile, MediaInfo info, CancellationToken cancel)
    {
        BusyText = "Preparo il video per l'anteprima…";
        BusyProgress = 0;
        IsBusyIndeterminate = false;
        var output = Path.Combine(_workFolder, $"anteprima-{Guid.NewGuid():N}.mp4");
        try
        {
            Directory.CreateDirectory(_workFolder);
            await FfmpegMedia.MakePreviewVideoAsync(_main.VideoTools, videoFile, output, info.Duration,
                new Progress<double>(p => BusyProgress = p * 100), cancel);
            return output;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The window's player could not open the video: converted once, then given up on.</summary>
    public async Task VideoFailedAsync()
    {
        if (IsBusy || VideoPath is null)
            return;
        if (!string.Equals(VideoPath, _originalVideoPath, StringComparison.OrdinalIgnoreCase) || _videoInfo is null)
        {
            VideoPath = null;
            StatusText = "Il video non si riesce a mostrare: scegli il pezzo dalla forma d'onda, ascoltandolo.";
            return;
        }
        var source = _originalVideoPath!;
        VideoPath = null;
        int generation = _loadGeneration;
        StartBusy("Preparo il video per l'anteprima…", indeterminate: false);
        try
        {
            var shown = await MakePlayableAsync(source, _videoInfo, _busyCancel!.Token);
            if (generation == _loadGeneration)
                VideoPath = shown;
            if (shown is null)
                StatusText = "Il video non si riesce a mostrare: scegli il pezzo dalla forma d'onda, ascoltandolo.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Anteprima del video annullata: scegli il pezzo dalla forma d'onda.";
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<bool> EnsureToolsAsync(bool downloader)
    {
        var tools = _main.VideoTools;
        bool needFfmpeg = !tools.IsFfmpegInstalled;
        bool needDownloader = downloader && !tools.IsDownloaderInstalled;
        if (!needFfmpeg && !needDownloader)
            return true;

        long bytes = (needFfmpeg ? VideoTools.FfmpegDownloadBytes : 0) + (needDownloader ? VideoTools.DownloaderDownloadBytes : 0);
        string what = (needFfmpeg, needDownloader) switch
        {
            (true, true) => "Per scaricare i video dai siti servono alcuni programmi gratuiti: yt-dlp (con Deno, che gli serve per YouTube) e FFmpeg",
            (false, true) => "Per scaricare i video dai siti serve yt-dlp, un programma gratuito (con Deno, che gli serve per YouTube)",
            _ => "Per leggere i video serve FFmpeg, un programma gratuito",
        };
        if (!_dialogs.Confirm(
                $"{what}: circa {bytes / 1024.0 / 1024:0} MB da GitHub, da scaricare una volta sola. " +
                "Poi l'audio si prepara tutto sul tuo PC.\n\nScaricare ora?",
                "Scarica", "Non ora"))
            return false;

        StartBusy("Scarico i programmi per i video…", indeterminate: false);
        var cancel = _busyCancel!.Token;
        try
        {
            if (needFfmpeg)
            {
                BusyText = "Scarico FFmpeg…";
                await tools.InstallFfmpegAsync(SharedHttp.Value, new Progress<double>(p => BusyProgress = p * 100), cancel);
            }
            if (needDownloader)
            {
                BusyText = "Scarico yt-dlp…";
                BusyProgress = 0;
                await tools.InstallDownloaderAsync(SharedHttp.Value, new Progress<double>(p => BusyProgress = p * 100), cancel);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download annullato.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = "Download non riuscito.";
            _dialogs.ShowError($"Non sono riuscito a scaricare i programmi per i video: {ex.Message}");
        }
        finally
        {
            EndBusy();
        }
        return false;
    }

    private static bool IsToolFailure(Exception ex) =>
        ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or OutOfMemoryException;

    // ---- Listening ------------------------------------------------------------------------------

    /// <summary>Plays the selection, or from the cursor to the end; stops when already playing.</summary>
    [RelayCommand]
    private void PlayPause()
    {
        if (IsPreviewing)
        {
            StopPreview();
            return;
        }
        if (Clip is not { } clip)
            return;
        int start = SelectionStart;
        int end = HasSelection ? SelectionEnd : clip.Frames;
        if (start >= clip.Frames)
            start = 0;
        try
        {
            _preview.Play(clip.ToSampleProvider(start, end), this, _main.OutputDeviceId);
            _playFrom = start;
            Playhead = start;
            IsPreviewing = true;
            _playheadTimer.Start();
        }
        catch (COMException ex)
        {
            StatusText = $"Impossibile ascoltare: {ex.Message}";
        }
    }

    public void StopPreview()
    {
        _preview.Stop();
        StopPlayhead();
    }

    private void UpdatePlayhead()
    {
        if (Clip is { } clip && IsPreviewing)
            Playhead = _playFrom + _preview.Position.TotalSeconds * clip.SampleRate;
    }

    private void StopPlayhead()
    {
        _playheadTimer.Stop();
        bool was = IsPreviewing;
        IsPreviewing = false;
        Playhead = -1;
        // Back to the frame of the selection.
        if (was && Clip is { } clip)
            VideoTime = clip.FramesToTime(_endMovedLast ? SelectionEnd : SelectionStart);
    }

    // ---- Adding ---------------------------------------------------------------------------------

    /// <summary>The view opens the sound editor on a sound just added.</summary>
    public event EventHandler<SoundItemViewModel>? EditRequested;

    private bool CanAdd() => HasSelection && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddAsync() => AddSelectionAsync(edit: false);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddAndEditAsync() => AddSelectionAsync(edit: true);

    private async Task AddSelectionAsync(bool edit)
    {
        if (Clip is not { } clip || !HasSelection || _audioPath is null || _audioInfo is null)
            return;
        var length = clip.FramesToTime(SelectionEnd - SelectionStart);
        if (length > AudioClip.MaxDuration)
        {
            StatusText = $"Il pezzo scelto dura più di {AudioClip.MaxDuration.TotalMinutes:0} minuti: scegline uno più corto.";
            return;
        }

        StopPreview();
        StartBusy("Preparo il suono…", indeterminate: true);
        var cancel = _busyCancel!.Token;
        try
        {
            AudioClip piece;
            if (_format.IsFullQuality)
            {
                piece = ClipEdits.Keep(clip, SelectionStart, SelectionEnd);
            }
            else
            {
                // Held at a lower quality: the piece is read again from the file, as it is there.
                int rate = PreviewFormat.SaveRateFor(_audioInfo.AudioSampleRate);
                int channels = Math.Clamp(_audioInfo.AudioChannels, 1, 2);
                var samples = await FfmpegMedia.ReadAudioAsync(_main.VideoTools, _audioPath, clip.FramesToTime(SelectionStart), length,
                    rate, channels, length, null, cancel);
                piece = new AudioClip(samples, rate, channels);
            }

            BusyText = "Salvo il suono…";
            var (sound, error) = await _main.AddVideoSoundAsync(piece, NewTitle, SelectedCategory.Category, SelectedFormat.Format, SelectedBitrate);
            if (error is not null)
            {
                _dialogs.ShowError(error);
                return;
            }
            StatusText = $"Aggiunto \"{sound!.Title}\" ({FormatTime(piece.Duration)}). Puoi scegliere un altro pezzo dello stesso video.";
            if (edit)
                EditRequested?.Invoke(this, sound);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Annullato.";
        }
        catch (Exception ex) when (IsToolFailure(ex))
        {
            StatusText = $"Impossibile preparare il suono: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    // ---- Busy -----------------------------------------------------------------------------------

    [RelayCommand]
    private void CancelBusy() => _busyCancel?.Cancel();

    private void StartBusy(string text, bool indeterminate)
    {
        _busyCancel?.Dispose();
        _busyCancel = new CancellationTokenSource();
        BusyText = text;
        BusyProgress = 0;
        IsBusyIndeterminate = indeterminate;
        IsBusy = true;
        AddCommand.NotifyCanExecuteChanged();
        AddAndEditCommand.NotifyCanExecuteChanged();
    }

    private void EndBusy()
    {
        IsBusy = false;
        AddCommand.NotifyCanExecuteChanged();
        AddAndEditCommand.NotifyCanExecuteChanged();
    }

    // ---- Time text ------------------------------------------------------------------------------

    private string FormatFrames(int frames) => Clip is null ? "" : FormatTime(Clip.FramesToTime(frames));

    private static string FormatTime(TimeSpan time) =>
        (time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss")) +
        CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + time.Milliseconds.ToString("000");

    /// <summary>"1:02,500", "62,5" or "62.5" (seconds); null when it cannot be read.</summary>
    private int? ParseFrames(string text)
    {
        if (Clip is not { } clip)
            return null;
        double seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length > 3)
            return null;
        foreach (var part in parts)
        {
            if (!double.TryParse(part.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0)
                return null;
            seconds = seconds * 60 + value;
        }
        return (int)Math.Clamp(Math.Round(seconds * clip.SampleRate), 0, clip.Frames);
    }

    // ---- Clean up -------------------------------------------------------------------------------

    // Left behind by a window closed while its player still held the file, or by a crash.
    private void DeleteOldWork()
    {
        try
        {
            if (!Directory.Exists(WorkRoot))
                return;
            foreach (var folder in Directory.EnumerateDirectories(WorkRoot))
            {
                if (!string.Equals(folder, _workFolder, StringComparison.OrdinalIgnoreCase))
                    TryDeleteFolder(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _playheadTimer.Stop();
        _busyCancel?.Cancel();
        _busyCancel?.Dispose();
        _preview.Dispose();
        Clip = null;
        TryDeleteFolder(_workFolder);
    }
}
