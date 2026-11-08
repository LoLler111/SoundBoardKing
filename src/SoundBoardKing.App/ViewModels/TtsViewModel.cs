using System.IO;
using System.Net.Http;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.Audio.Speech;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.ViewModels;

/// <summary>
/// The "Sintesi vocale" window: write something and a voice says it, into the microphone too. The voices of
/// Windows are there already; the neural ones (Piper, Kokoro) are downloaded from here.
/// </summary>
public sealed partial class TtsViewModel(MainViewModel main) : ObservableObject, IDisposable
{
    private const int HistoryLength = 30;
    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SoundBoardKing");
        return http;
    });

    private readonly Dictionary<VoicePackageViewModel, CancellationTokenSource> _downloads = [];
    private CancellationTokenSource? _speaking;

    [ObservableProperty]
    private IReadOnlyList<TtsVoice> voices = [];

    [ObservableProperty]
    private TtsVoice? selectedVoice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SpeakCommand))]
    private string text = "";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "";

    public ObservableCollection<string> History { get; } = new(main.Local.TtsHistory);

    public int MinRate => TextToSpeech.MinRate;
    public int MaxRate => TextToSpeech.MaxRate;

    public double Rate
    {
        get => main.Local.TtsRate;
        set
        {
            int rate = (int)Math.Round(Math.Clamp(value, MinRate, MaxRate));
            if (rate == main.Local.TtsRate)
                return;
            main.Local.TtsRate = rate;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RateText));
            main.SaveLocalSettings();
        }
    }

    /// <summary>Compute the neural voices on the graphics card; off, always on the processor.</summary>
    public bool UseGpu
    {
        get => main.Local.TtsUseGpu;
        set
        {
            if (value == main.Local.TtsUseGpu)
                return;
            main.Local.TtsUseGpu = value;
            main.SaveLocalSettings();
            OnPropertyChanged();
            // The models are loaded again, where asked, at the next phrase.
            _ = Task.Run(main.ApplyRvcVoices);
        }
    }

    public string RateText => main.Local.TtsRate switch
    {
        0 => "normale",
        > 0 and var r => $"+{r}",
        var r => $"{r}",
    };

    /// <summary>Where "Parla" sends the voice, as for the sounds.</summary>
    public string SpeakHint => main.PlayOnMic && main.IsMicAvailable
        ? "In cuffia e nel microfono"
        : main.IsMicAvailable ? "Solo in cuffia (i suoni nel microfono sono spenti)" : "Solo in cuffia (modulo microfono non installato)";

    /// <summary>The neural voices to download (Piper, Kokoro): free, and offline once downloaded.</summary>
    public IReadOnlyList<VoicePackageViewModel> Packages { get; } =
        VoiceCatalog.Packages.Select(p => new VoicePackageViewModel(p, main.Speech.Catalog)).ToList();

    public async Task LoadVoicesAsync(string? select = null)
    {
        StatusText = "Cerco le voci installate…";
        string? current = select ?? SelectedVoice?.Name ?? main.Local.TtsVoice;
        try
        {
            Voices = await main.Speech.GetVoicesAsync();
        }
        catch (Exception ex)
        {
            Voices = [];
            StatusText = $"Impossibile leggere le voci: {ex.Message}";
            return;
        }
        SelectedVoice = Voices.FirstOrDefault(v => v.Name == current)
            ?? Voices.FirstOrDefault(v => v.Language.StartsWith("Italiano", StringComparison.OrdinalIgnoreCase))
            ?? Voices.FirstOrDefault();
        StatusText = Voices.Count switch
        {
            0 => "Nessuna voce installata: scaricane una qui sotto, o aggiungile da Impostazioni di Windows › Ora e lingua › Voce.",
            1 => "1 voce installata.",
            var n => $"{n} voci installate.",
        };
    }

    [RelayCommand]
    private async Task DownloadPackage(VoicePackageViewModel? package)
    {
        if (package is null || !package.CanDownload)
            return;
        var cancel = new CancellationTokenSource();
        _downloads[package] = cancel;
        package.IsDownloading = true;
        package.Progress = 0;
        StatusText = $"Scarico {package.Title}…";
        try
        {
            // A better model replaces the one in use: freed first, the next phrase loads the new one.
            if (package.IsInstalled)
                await Task.Run(main.Speech.ReleaseModels);
            await main.Speech.Catalog.DownloadAsync(package.Package, SharedHttp.Value,
                new Progress<double>(p => package.Progress = p * 100), cancel.Token);
            foreach (var other in Packages)
                other.Refresh();
            await LoadVoicesAsync(select: package.Package.Speakers.FirstOrDefault()?.Name);
            StatusText = package.Package.Speakers.Count > 0 ? $"{package.Title}: pronta. Scegli la voce e scrivi qualcosa." : $"{package.Title}: installati.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download annullato.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Download non riuscito: {ex.Message}";
        }
        finally
        {
            _downloads.Remove(package);
            cancel.Dispose();
            package.IsDownloading = false;
            package.Refresh();
        }
    }

    [RelayCommand]
    private void CancelDownload(VoicePackageViewModel? package)
    {
        if (package is not null && _downloads.TryGetValue(package, out var cancel))
            cancel.Cancel();
    }

    [RelayCommand]
    private async Task DeletePackage(VoicePackageViewModel? package)
    {
        if (package is null || !package.IsInstalled)
            return;
        try
        {
            // The model may be loaded: let it go before its files.
            await Task.Run(main.Speech.ReleaseModels);
            main.Speech.Catalog.Delete(package.Package);
            StatusText = $"{package.Title}: eliminata.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Impossibile eliminare {package.Title}: {ex.Message}";
        }
        package.Refresh();
        await LoadVoicesAsync();
    }

    /// <summary>ContentVec and RMVPE, downloaded once for every RVC voice.</summary>
    public VoicePackageViewModel RvcSupport { get; } = new(VoiceCatalog.RvcSupport, main.Speech.Catalog);

    /// <summary>The RVC voices of this PC.</summary>
    public ObservableCollection<RvcVoiceItem> RvcVoices { get; } =
        new(main.Local.RvcVoices.Select(v => new RvcVoiceItem(v)));

    /// <summary>The dialog's model for a new RVC voice, or for changing <paramref name="existing"/>.</summary>
    public RvcVoiceEditorViewModel EditorFor(RvcVoiceItem? existing) => new(
        Voices.Where(v => v.Engine != TtsEngine.Rvc).Select(v => v.Name).ToList(),
        Voices.Select(v => v.Name).Where(n => n != existing?.Setting.Name).ToList(),
        existing?.Setting);

    /// <summary>Adds <paramref name="saved"/>, or puts it in place of <paramref name="replaced"/>.</summary>
    public async Task SaveRvcVoiceAsync(RvcVoiceItem? replaced, RvcVoiceSetting saved)
    {
        var list = main.Local.RvcVoices;
        int at = replaced is null ? -1 : list.IndexOf(replaced.Setting);
        if (at >= 0)
        {
            list[at] = saved;
            RvcVoices[RvcVoices.IndexOf(replaced!)] = new RvcVoiceItem(saved);
        }
        else
        {
            list.Add(saved);
            RvcVoices.Add(new RvcVoiceItem(saved));
        }
        main.SaveLocalSettings();
        main.ApplyRvcVoices();
        // A changed model or index is loaded again at the next phrase.
        await Task.Run(main.Speech.ReleaseModels);
        await LoadVoicesAsync(select: saved.Name);
        StatusText = main.Speech.IsRvcReady
            ? $"{saved.Name}: pronta."
            : $"{saved.Name}: salvata. Per farla parlare scarica i componenti RVC qui sotto.";
    }

    [RelayCommand]
    private async Task RemoveRvcVoice(RvcVoiceItem? item)
    {
        if (item is null)
            return;
        main.Local.RvcVoices.Remove(item.Setting);
        RvcVoices.Remove(item);
        main.SaveLocalSettings();
        main.ApplyRvcVoices();
        await LoadVoicesAsync();
        StatusText = $"{item.Setting.Name}: tolta (il file del modello resta dov'era).";
    }

    partial void OnSelectedVoiceChanged(TtsVoice? value)
    {
        if (value is null || value.Name == main.Local.TtsVoice)
            return;
        main.Local.TtsVoice = value.Name;
        main.SaveLocalSettings();
    }

    private bool CanSpeak(PlayTarget target) => !string.IsNullOrWhiteSpace(Text);

    /// <summary>Says the text written; the parameter is where (default, headphones only, microphone only).</summary>
    [RelayCommand(CanExecute = nameof(CanSpeak))]
    private Task Speak(PlayTarget target) => SayAsync(Text, target);

    /// <summary>Says a recent phrase again, where "Parla" would.</summary>
    [RelayCommand]
    private Task SpeakAgain(string? phrase) => phrase is null ? Task.CompletedTask : SayAsync(phrase, PlayTarget.Default);

    [RelayCommand]
    private void RemoveFromHistory(string? phrase)
    {
        if (phrase is not null && History.Remove(phrase))
            SaveHistory();
    }

    [RelayCommand]
    private void Stop()
    {
        _speaking?.Cancel();
        main.StopCommand.Execute(null);
    }

    private async Task SayAsync(string phrase, PlayTarget target)
    {
        phrase = phrase.Trim();
        if (phrase.Length == 0)
            return;
        if (SelectedVoice is not { } voice)
        {
            StatusText = "Scegli prima una voce.";
            return;
        }

        // A new phrase replaces one still being prepared.
        _speaking?.Cancel();
        var speaking = _speaking = new CancellationTokenSource();
        IsBusy = true;
        StatusText = $"{voice.Name} sta preparando la frase…";
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var problem = await main.SpeakAsync(phrase, voice, main.Local.TtsRate, target, speaking.Token);
            watch.Stop();
            // How long the phrase took and where, so the difference the graphics card makes shows.
            string device = main.Speech.LastDevice is { Length: > 0 } where ? ", " + where : "";
            StatusText = problem ?? $"{voice.Name}: «{(phrase.Length > 40 ? phrase[..37] + "…" : phrase)}» · {watch.Elapsed.TotalSeconds:0.0} s{device}";
            if (problem is null)
                Remember(phrase);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (_speaking == speaking)
            {
                IsBusy = false;
                _speaking = null;
            }
            speaking.Dispose();
        }
    }

    private void Remember(string phrase)
    {
        int index = History.IndexOf(phrase);
        if (index == 0)
            return;
        if (index > 0)
            History.RemoveAt(index);
        History.Insert(0, phrase);
        while (History.Count > HistoryLength)
            History.RemoveAt(History.Count - 1);
        SaveHistory();
    }

    private void SaveHistory()
    {
        main.Local.TtsHistory = [.. History];
        main.SaveLocalSettings();
    }

    public void Dispose()
    {
        _speaking?.Cancel();
        foreach (var download in _downloads.Values)
            download.Cancel();
        // The neural models take hundreds of MB: only while the window is open.
        Task.Run(main.Speech.ReleaseModels);
    }
}

/// <summary>A row of "Voci RVC".</summary>
public sealed class RvcVoiceItem(RvcVoiceSetting setting)
{
    public RvcVoiceSetting Setting { get; } = setting;
    public string Name => Setting.Name;

    public string Summary =>
        $"legge {Setting.BaseVoice} · tono {(Setting.Pitch == 0 ? "0" : Setting.Pitch.ToString("+0;-0"))} · indice {Setting.IndexRate * 100:0}%"
        + (Math.Abs(Setting.Intonation - 1) < 0.005f ? "" : $" · intonazione {Setting.Intonation * 100:0}%")
        + (Setting.IndexPath is null ? " (senza indice)" : "");

    public string ModelFile => Setting.ModelPath;
}
