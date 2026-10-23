using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.App.Services;
using SoundBoardKing.Audio;
using Microsoft.ML.OnnxRuntime;
using SoundBoardKing.Audio.Editing;

namespace SoundBoardKing.App.ViewModels;

public sealed record FadeCurveOption(FadeCurve Curve, string Label);

/// <summary>
/// The sound editor: the whole sound in memory, edits that each make a new copy (so undo is just
/// going back to the previous one), listening in the headphones, and saving in the chosen format.
/// </summary>
public sealed partial class SoundEditorViewModel : ObservableObject, IDisposable
{
    // Undo keeps whole copies: past this much memory the oldest steps are dropped.
    private const long UndoBudgetBytes = 768L << 20;

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SoundBoardKing/{UpdateService.CurrentVersion}");
        return http;
    });

    // What Copia/Taglia took, for pasting here or in the next sound opened.
    private static AudioClip? _clipboard;

    private readonly MainViewModel _main;
    private readonly IDialogService _dialogs;
    private readonly PreviewPlayer _preview = new();
    private readonly DispatcherTimer _playheadTimer;
    private readonly List<AudioClip> _undo = [];
    private readonly List<AudioClip> _redo = [];
    private AudioClip? _savedClip;
    private int _playFrom;
    private CancellationTokenSource? _busyCancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(DurationText), nameof(FormatText))]
    private AudioClip? clip;

    [ObservableProperty]
    private int selectionStart;

    [ObservableProperty]
    private int selectionEnd;

    [ObservableProperty]
    private double playhead = -1;

    [ObservableProperty]
    private bool isPreviewing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool isBusy;

    [ObservableProperty]
    private string busyText = "";

    [ObservableProperty]
    private double busyProgress;

    [ObservableProperty]
    private bool isBusyCancellable;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private IReadOnlyList<ExportFormatInfo> formats = AudioExport.Formats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLossy))]
    private ExportFormatInfo selectedFormat = AudioExport.Formats[0];

    [ObservableProperty]
    private int selectedBitrate = AudioExport.DefaultBitrate;

    [ObservableProperty]
    private FadeCurveOption selectedCurve;

    [ObservableProperty]
    private string newTitle;

    public SoundEditorViewModel(MainViewModel main, SoundItemViewModel sound, IDialogService dialogs)
    {
        _main = main;
        _dialogs = dialogs;
        Sound = sound;
        newTitle = sound.Title + " (modificato)";
        selectedCurve = FadeCurves[1];
        _preview.Volume = main.PreviewVolume;
        _preview.Ended += (_, _) => StopPlayhead();
        _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();
    }

    public SoundItemViewModel Sound { get; }
    public string Title => $"Modifica - {Sound.Title}";

    public IReadOnlyList<int> Bitrates => AudioExport.Bitrates;

    public IReadOnlyList<FadeCurveOption> FadeCurves { get; } =
    [
        new(FadeCurve.Linear, "Lineare"),
        new(FadeCurve.Smooth, "Morbida (a S)"),
        new(FadeCurve.Exponential, "Esponenziale (coda lunga)"),
        new(FadeCurve.Logarithmic, "Logaritmica (rapida)"),
    ];

    public bool IsReady => Clip is not null && !IsBusy;
    public bool IsLossy => !SelectedFormat.IsLossless;
    public bool HasSelection => SelectionEnd > SelectionStart;
    public bool IsDirty => Clip is not null && !ReferenceEquals(Clip, _savedClip);

    public string DurationText => Clip is null ? "" : FormatTime(Clip.Duration);

    public string FormatText => Clip is null ? "" : $"{Clip.SampleRate} Hz, {(Clip.Channels == 1 ? "mono" : "stereo")}";

    /// <summary>Volume of the preview, 0..100; shared with the Myinstants preview.</summary>
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

    // ---- Selection as text ----------------------------------------------------------------------

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

    partial void OnSelectionStartChanged(int value) => SelectionChanged();

    partial void OnSelectionEndChanged(int value) => SelectionChanged();

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionStartText));
        OnPropertyChanged(nameof(SelectionEndText));
        OnPropertyChanged(nameof(SelectionLengthText));
        KeepSelectionCommand.NotifyCanExecuteChanged();
        DeleteSelectionCommand.NotifyCanExecuteChanged();
        SilenceSelectionCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
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

    [RelayCommand]
    private void SelectAll() => SetSelection(0, Clip?.Frames ?? 0);

    // ---- Loading --------------------------------------------------------------------------------

    public async Task LoadAsync()
    {
        StartBusy("Apro il suono…", cancellable: false);
        try
        {
            var path = Sound.Model.Path;
            var (clip, formats) = await Task.Run(() => (AudioClip.Load(path), AudioExport.AvailableFormats()));
            Formats = formats;
            // The same format as the file, at about its bitrate, so replacing it changes nothing else.
            SelectedFormat = formats.FirstOrDefault(f => f.Format == AudioExport.FormatOf(path)) ?? formats[0];
            if (!SelectedFormat.IsLossless && clip.Duration.TotalSeconds > 1)
            {
                double kbps = new FileInfo(path).Length * 8 / clip.Duration.TotalSeconds / 1000;
                SelectedBitrate = Bitrates.FirstOrDefault(b => b >= kbps * 0.9, Bitrates[^1]);
            }
            Clip = _savedClip = clip;
            StatusText = "Trascina sulla forma d'onda per selezionare; Spazio ascolta, Ctrl+rotella ingrandisce.";
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            StatusText = $"Impossibile aprire il suono: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

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
        IsPreviewing = false;
        Playhead = -1;
    }

    // ---- Edits ----------------------------------------------------------------------------------

    /// <summary>Makes <paramref name="result"/> the current sound, keeping the old one for undo.</summary>
    private void Apply(AudioClip result, int selectionStart, int selectionEnd, string done)
    {
        StopPreview();
        if (Clip is not null)
            _undo.Add(Clip);
        _redo.Clear();
        TrimUndo();
        Clip = result;
        SetSelection(selectionStart, selectionEnd);
        StatusText = done;
        HistoryChanged();
    }

    private void TrimUndo()
    {
        long bytes = _undo.Sum(c => (long)c.Samples.Length * sizeof(float));
        while (_undo.Count > 1 && bytes > UndoBudgetBytes)
        {
            bytes -= (long)_undo[0].Samples.Length * sizeof(float);
            _undo.RemoveAt(0);
        }
    }

    private void HistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDirty));
    }

    private bool CanUndo() => _undo.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0 || Clip is null)
            return;
        StopPreview();
        _redo.Add(Clip);
        Clip = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        SetSelection(SelectionStart, SelectionEnd);
        StatusText = "Annullato.";
        HistoryChanged();
    }

    private bool CanRedo() => _redo.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0 || Clip is null)
            return;
        StopPreview();
        _undo.Add(Clip);
        Clip = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        SetSelection(SelectionStart, SelectionEnd);
        StatusText = "Ripristinato.";
        HistoryChanged();
    }

    private bool CanEditSelection() => HasSelection;

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void KeepSelection()
    {
        if (Clip is not { } clip || !HasSelection)
            return;
        Apply(ClipEdits.Keep(clip, SelectionStart, SelectionEnd), 0, 0, "Tenuta solo la parte selezionata.");
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void DeleteSelection()
    {
        if (Clip is not { } clip || !HasSelection)
            return;
        int at = SelectionStart;
        Apply(ClipEdits.Delete(clip, SelectionStart, SelectionEnd), at, at, "Parte selezionata eliminata.");
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void SilenceSelection()
    {
        if (Clip is not { } clip || !HasSelection)
            return;
        Apply(ClipEdits.Silence(clip, SelectionStart, SelectionEnd), SelectionStart, SelectionEnd, "Parte selezionata resa silenziosa.");
    }

    [RelayCommand]
    private void InsertSilence()
    {
        if (Clip is not { } clip)
            return;
        var answer = _dialogs.AskText("Inserisci silenzio", "Quanti secondi di silenzio inserire nel punto del cursore?", "1");
        if (answer is null)
            return;
        if (!TryParseNumber(answer, out double seconds) || seconds <= 0 || seconds > 600)
        {
            StatusText = "Scrivi una durata in secondi, ad esempio 0,5.";
            return;
        }
        var silence = AudioClip.Silence((int)(seconds * clip.SampleRate), clip.SampleRate, clip.Channels);
        int at = SelectionStart;
        Apply(ClipEdits.Insert(clip, at, silence), at, at + silence.Frames, $"Inseriti {seconds.ToString("0.###", CultureInfo.CurrentCulture)} s di silenzio.");
    }

    /// <summary>Puts another audio file at the cursor (in place of the selection, if any).</summary>
    [RelayCommand]
    private async Task InsertFileAsync()
    {
        if (Clip is null)
            return;
        var file = _dialogs.OpenAudioFiles().FirstOrDefault();
        if (file is null)
            return;
        StartBusy("Apro il file da inserire…", cancellable: false);
        AudioClip other;
        try
        {
            other = await Task.Run(() => AudioClip.Load(file));
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            StatusText = $"Impossibile aprire \"{Path.GetFileName(file)}\": {ex.Message}";
            return;
        }
        finally
        {
            EndBusy();
        }
        var clip = Clip!;
        int at = SelectionStart;
        var result = ClipEdits.Replace(clip, SelectionStart, SelectionEnd, other);
        int inserted = result.Frames - (clip.Frames - (SelectionEnd - SelectionStart));
        Apply(result, at, at + inserted, $"Inserito \"{Path.GetFileName(file)}\".");
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void Copy()
    {
        if (Clip is not { } clip || !HasSelection)
            return;
        _clipboard = ClipEdits.Keep(clip, SelectionStart, SelectionEnd);
        PasteCommand.NotifyCanExecuteChanged();
        StatusText = $"Copiati {FormatFrames(SelectionEnd - SelectionStart)}: incollali con Ctrl+V, anche in un altro suono.";
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void Cut()
    {
        if (Clip is not { } clip || !HasSelection)
            return;
        _clipboard = ClipEdits.Keep(clip, SelectionStart, SelectionEnd);
        PasteCommand.NotifyCanExecuteChanged();
        int at = SelectionStart;
        Apply(ClipEdits.Delete(clip, SelectionStart, SelectionEnd), at, at, "Parte tagliata: incollala con Ctrl+V.");
    }

    private bool CanPaste() => _clipboard is not null;

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (Clip is not { } clip || _clipboard is not { } pasted)
            return;
        int at = SelectionStart;
        var result = ClipEdits.Replace(clip, SelectionStart, SelectionEnd, pasted);
        int inserted = result.Frames - (clip.Frames - (SelectionEnd - SelectionStart));
        Apply(result, at, at + inserted, "Incollato.");
    }

    /// <summary>On the selection, or from the start up to the cursor.</summary>
    [RelayCommand]
    private void FadeIn()
    {
        if (Clip is not { } clip)
            return;
        var (start, end) = HasSelection ? (SelectionStart, SelectionEnd) : (0, SelectionStart);
        if (end - start < 2)
        {
            StatusText = "Seleziona la parte da sfumare, o metti il cursore dove la dissolvenza deve finire.";
            return;
        }
        Apply(ClipEdits.Fade(clip, start, end, FadeDirection.In, SelectedCurve.Curve), start, end,
            $"Dissolvenza in entrata di {FormatFrames(end - start)}.");
    }

    /// <summary>On the selection, or from the cursor to the end.</summary>
    [RelayCommand]
    private void FadeOut()
    {
        if (Clip is not { } clip)
            return;
        var (start, end) = HasSelection ? (SelectionStart, SelectionEnd) : (SelectionStart, clip.Frames);
        if (end - start < 2)
        {
            StatusText = "Seleziona la parte da sfumare, o metti il cursore dove la dissolvenza deve iniziare.";
            return;
        }
        Apply(ClipEdits.Fade(clip, start, end, FadeDirection.Out, SelectedCurve.Curve), start, end,
            $"Dissolvenza in uscita di {FormatFrames(end - start)}.");
    }

    // The selection, or the whole sound without one.
    private (int Start, int End) Target(AudioClip clip) => HasSelection ? (SelectionStart, SelectionEnd) : (0, clip.Frames);

    [RelayCommand]
    private void Amplify()
    {
        if (Clip is not { } clip)
            return;
        var (start, end) = Target(clip);
        var answer = _dialogs.AskText("Volume",
            (HasSelection ? "Di quanti decibel cambiare il volume della selezione?" : "Di quanti decibel cambiare il volume del suono?") +
            "\n+6 è circa il doppio, -6 la metà.", "+3");
        if (answer is null)
            return;
        if (!TryParseNumber(answer.Replace("dB", "", StringComparison.OrdinalIgnoreCase), out double db) || Math.Abs(db) > 40)
        {
            StatusText = "Scrivi i decibel, ad esempio +3 o -6.";
            return;
        }
        var result = ClipEdits.Amplify(clip, start, end, db);
        var note = ClipEdits.PeakDb(result, start, end) > 0 ? " Attenzione: ora distorce, prova con meno o con Normalizza." : "";
        Apply(result, SelectionStart, SelectionEnd, $"Volume cambiato di {db.ToString("+0.#;-0.#", CultureInfo.CurrentCulture)} dB." + note);
    }

    [RelayCommand]
    private void Normalize()
    {
        if (Clip is not { } clip)
            return;
        var (start, end) = Target(clip);
        if (ClipEdits.PeakDb(clip, start, end) is null)
        {
            StatusText = "Qui c'è solo silenzio.";
            return;
        }
        Apply(ClipEdits.Normalize(clip, start, end), SelectionStart, SelectionEnd, "Volume portato al massimo senza distorcere (-1 dB).");
    }

    // ---- Voice ----------------------------------------------------------------------------------

    [RelayCommand]
    private Task IsolateVoiceAsync() => SeparateWithModelAsync(VoicePart.Voice);

    [RelayCommand]
    private Task RemoveVoiceAsync() => SeparateWithModelAsync(VoicePart.Background);

    [RelayCommand]
    private async Task QuickVoiceAsync()
    {
        if (Clip is not { } clip)
            return;
        var (start, end) = Target(clip);
        StartBusy("Filtro rapido sulla voce…", cancellable: false);
        try
        {
            var part = ClipEdits.Keep(clip, start, end);
            var voice = await Task.Run(() => CenterVoiceFilter.Isolate(part, VoicePart.Voice, CancellationToken.None));
            ApplyPart(clip, start, end, voice,
                clip.Channels == 1 ? "Filtro rapido: tenuta la banda della voce. Su un suono mono la separazione vera la fa \"Isola voce\"."
                : "Filtro rapido: tenuto il centro, dove di solito c'è la voce. Se senti ancora musica, prova \"Isola voce\".");
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task SeparateWithModelAsync(VoicePart part)
    {
        if (Clip is not { } clip)
            return;
        if (!VoiceModel.IsInstalled && !await DownloadModelAsync())
            return;

        var (start, end) = Target(clip);
        StartBusy(part == VoicePart.Voice ? "Isolo la voce…" : "Tolgo la voce…", cancellable: true);
        var cancel = _busyCancel!.Token;
        var progress = new Progress<double>(p => BusyProgress = p * 100);
        try
        {
            var section = ClipEdits.Keep(clip, start, end);
            var result = await Task.Run(() => VoiceSeparator.Isolate(section, part, progress, cancel), cancel);
            ApplyPart(clip, start, end, result, part == VoicePart.Voice
                ? "Voce isolata: musica ed effetti tolti. Ascolta e, se serve, Annulla."
                : "Voce tolta: resta solo lo sfondo.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Annullato.";
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or InvalidOperationException or OutOfMemoryException)
        {
            StatusText = $"Separazione non riuscita: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    // Puts a processed section back in place of the original one, keeping it selected.
    private void ApplyPart(AudioClip clip, int start, int end, AudioClip processed, string done)
    {
        var result = start == 0 && end == clip.Frames
            ? processed.ConvertTo(clip.SampleRate, clip.Channels)
            : ClipEdits.Replace(clip, start, end, processed);
        int length = result.Frames - (clip.Frames - (end - start));
        Apply(result, HasSelection ? start : 0, HasSelection ? start + length : 0, done);
    }

    private async Task<bool> DownloadModelAsync()
    {
        double mb = VoiceModel.SizeBytes / 1024.0 / 1024;
        if (!_dialogs.Confirm(
                $"Per separare la voce dal resto serve un modello di intelligenza artificiale di {mb:0} MB, " +
                "da scaricare una volta sola (da GitHub, dal progetto Ultimate Vocal Remover). Poi funziona anche senza internet, " +
                "tutto sul tuo PC.\n\nScaricarlo ora?",
                "Scarica", "Non ora"))
            return false;

        StartBusy("Scarico il modello per la voce…", cancellable: true);
        var cancel = _busyCancel!.Token;
        try
        {
            await VoiceModel.DownloadAsync(SharedHttp.Value, new Progress<double>(p => BusyProgress = p * 100), cancel);
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download annullato.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = $"Download non riuscito: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
        return false;
    }

    // ---- Saving ---------------------------------------------------------------------------------

    /// <summary>The editor is done and closes.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private async Task SaveAsNewAsync()
    {
        if (Clip is not { } clip)
            return;
        StopPreview();
        StartBusy("Salvo il nuovo suono…", cancellable: false);
        string? error;
        try
        {
            error = await _main.AddEditedSoundAsync(Sound, clip, SelectedFormat.Format, SelectedBitrate, NewTitle);
        }
        finally
        {
            EndBusy();
        }
        Finish(clip, error);
    }

    [RelayCommand]
    private async Task ReplaceOriginalAsync()
    {
        if (Clip is not { } clip)
            return;
        var newExtension = SelectedFormat.Extension;
        bool formatChanges = !string.Equals(Path.GetExtension(Sound.Model.Path), newExtension, StringComparison.OrdinalIgnoreCase);
        var question = $"Sostituire il file di \"{Sound.Title}\" con la versione modificata?" +
            (formatChanges ? $"\n\nIl file diventa {newExtension}; quello vecchio va nel Cestino." : "\n\nIl file originale viene sovrascritto.");
        if (!_dialogs.Confirm(question, "Sostituisci", "Annulla"))
            return;

        StopPreview();
        StartBusy("Salvo…", cancellable: false);
        string? error;
        try
        {
            error = await _main.ReplaceSoundFileAsync(Sound, clip, SelectedFormat.Format, SelectedBitrate);
        }
        finally
        {
            EndBusy();
        }
        Finish(clip, error);
    }

    private void Finish(AudioClip saved, string? error)
    {
        if (error is not null)
        {
            _dialogs.ShowError(error);
            return;
        }
        _savedClip = saved;
        OnPropertyChanged(nameof(IsDirty));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelBusy() => _busyCancel?.Cancel();

    private void StartBusy(string text, bool cancellable)
    {
        _busyCancel?.Dispose();
        _busyCancel = new CancellationTokenSource();
        BusyText = text;
        BusyProgress = 0;
        IsBusyCancellable = cancellable;
        IsBusy = true;
        HistoryChanged();
    }

    private void EndBusy()
    {
        IsBusy = false;
        HistoryChanged();
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
        text = text.Trim();
        double seconds = 0;
        var parts = text.Split(':');
        if (parts.Length > 3)
            return null;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!TryParseNumber(parts[i], out double value) || value < 0)
                return null;
            seconds = seconds * 60 + value;
        }
        return (int)Math.Clamp(Math.Round(seconds * clip.SampleRate), 0, clip.Frames);
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public void Dispose()
    {
        _playheadTimer.Stop();
        _busyCancel?.Cancel();
        _busyCancel?.Dispose();
        _preview.Dispose();
        // The window only closes when no separation is running.
        VoiceSeparator.Release();
    }
}
