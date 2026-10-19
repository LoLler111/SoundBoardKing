using System.Collections.ObjectModel;
using SoundBoardKing.Core.Models;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.Audio;
using SoundBoardKing.Core.Myinstants;

namespace SoundBoardKing.App.ViewModels;

/// <summary>One result of the Myinstants search.</summary>
public sealed partial class MyinstantsResultViewModel(MyinstantsSound sound) : ObservableObject
{
    public MyinstantsSound Sound { get; } = sound;
    public string Title => Sound.Title;

    /// <summary>The mp3 and its loudness once downloaded; only in memory, gone with the window.</summary>
    public byte[]? Data { get; set; }
    public LoudnessInfo? Loudness { get; set; }

    [ObservableProperty]
    private bool isPreviewing;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isAdded;
}

/// <summary>
/// Search Myinstants, listen in the headphones with a volume of its own, add to the library. Nothing
/// touches the disk until "add": previews play from memory.
/// </summary>
public sealed partial class MyinstantsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly PreviewPlayer _preview = new();
    private CancellationTokenSource _searchCancel = new();
    private string _lastQuery = "";
    private int _lastPage;

    public MyinstantsViewModel(MainViewModel main)
    {
        _main = main;
        _preview.Volume = main.PreviewVolume;
        _preview.Ended += (_, tag) =>
        {
            if (tag is MyinstantsResultViewModel result)
                result.IsPreviewing = false;
        };
        LastCategory = main.SelectedCategoryModel;
    }

    /// <summary>
    /// The category chosen for the last addition (null: library only), ticked in the menu so a
    /// series of sounds for the same category is quick; it starts from the one selected in the main window.
    /// </summary>
    public Category? LastCategory { get; private set; }

    public IReadOnlyList<(Category Category, int Depth)> Categories => _main.CategoriesInOrder();

    public Category? AskNewCategory() => _main.AskNewCategory();

    public ObservableCollection<MyinstantsResultViewModel> Results { get; } = [];

    [ObservableProperty]
    private string query = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool isSearching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool hasMore;

    [ObservableProperty]
    private string statusText = "";

    /// <summary>Preview volume 0-100, remembered on this PC.</summary>
    public double PreviewVolume
    {
        get => _preview.Volume * 100;
        set
        {
            float volume = (float)Math.Clamp(value / 100, 0, 1);
            if (Math.Abs(volume - _preview.Volume) < 0.0001f)
                return;
            _preview.Volume = volume;
            _main.PreviewVolume = volume;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private Task Search() => LoadAsync(Query.Trim(), 1);

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMore() => LoadAsync(_lastQuery, _lastPage + 1);

    private bool CanLoadMore() => HasMore && !IsSearching;

    private async Task LoadAsync(string query, int page)
    {
        _searchCancel.Cancel();
        _searchCancel = new CancellationTokenSource();
        var cancel = _searchCancel.Token;

        if (page == 1)
        {
            _preview.Stop();
            Results.Clear();
            HasMore = false;
        }
        IsSearching = true;
        StatusText = query.Length == 0 ? "Carico i suoni di tendenza…" : $"Cerco \"{query}\"…";
        try
        {
            IReadOnlyList<MyinstantsSound> sounds;
            bool more;
            if (MyinstantsClient.IsSoundLink(query))
            {
                // A link pasted in the search box: show that one sound.
                var sound = await _main.Myinstants.ResolveAsync(query, cancel);
                sounds = sound is null ? [] : [sound];
                more = false;
            }
            else
            {
                var result = await _main.Myinstants.SearchAsync(query, page, cancel);
                sounds = result.Sounds;
                more = result.HasMore;
            }
            if (cancel.IsCancellationRequested)
                return;

            var known = Results.Select(r => r.Sound.SoundUrl).ToHashSet();
            foreach (var sound in sounds.Where(s => known.Add(s.SoundUrl)))
                Results.Add(new MyinstantsResultViewModel(sound) { IsAdded = _main.FindMyinstantsSound(sound) is not null });

            _lastQuery = query;
            _lastPage = page;
            HasMore = more;
            StatusText = Results.Count == 0 ? "Nessun suono trovato."
                : query.Length == 0 ? $"{Results.Count} suoni di tendenza."
                : $"{Results.Count} suoni trovati.";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusText = $"Myinstants non risponde: {ex.Message}";
        }
        finally
        {
            if (!cancel.IsCancellationRequested)
                IsSearching = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Preview(MyinstantsResultViewModel? result)
    {
        if (result is null)
            return;
        if (result.IsPreviewing)
        {
            _preview.Stop();
            return;
        }
        if (!await EnsureDataAsync(result))
            return;
        try
        {
            _preview.Play(result.Data!, result, _main.OutputDeviceId, global::SoundBoardKing.Audio.Loudness.GainFor(result.Loudness?.Lufs, result.Loudness?.PeakDb));
            result.IsPreviewing = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            StatusText = $"Impossibile ascoltare \"{result.Title}\": {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopPreview() => _preview.Stop();

    /// <summary>Downloads (if the preview has not already) and adds the sound to <paramref name="category"/>; null: library only.</summary>
    public async Task AddAsync(MyinstantsResultViewModel result, Category? category)
    {
        if (result.IsAdded || result.IsBusy)
            return;
        LastCategory = category;
        if (!await EnsureDataAsync(result))
            return;
        result.IsBusy = true;
        try
        {
            var added = await _main.AddMyinstantsSoundAsync(result.Sound, result.Data!, result.Loudness, category);
            if (added is not null)
            {
                result.IsAdded = true;
                StatusText = category is null
                    ? $"\"{added.Title}\" aggiunto alla libreria."
                    : $"\"{added.Title}\" aggiunto a \"{category.Name}\".";
            }
            else
            {
                StatusText = _main.StatusText;
            }
        }
        finally
        {
            result.IsBusy = false;
        }
    }

    /// <summary>Downloads the mp3 into memory once, for both the preview and the addition.</summary>
    private async Task<bool> EnsureDataAsync(MyinstantsResultViewModel result)
    {
        if (result.Data is not null)
            return true;
        if (result.IsBusy)
            return false;
        result.IsBusy = true;
        try
        {
            var data = await _main.Myinstants.DownloadAsync(result.Sound);
            var loudness = await Task.Run(() => PreviewPlayer.TryMeasure(data));
            if (loudness is null)
            {
                StatusText = $"\"{result.Title}\" non è un file audio valido.";
                return false;
            }
            result.Data = data;
            result.Loudness = loudness;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            StatusText = $"Impossibile scaricare \"{result.Title}\": {ex.Message}";
            return false;
        }
        finally
        {
            result.IsBusy = false;
        }
    }

    public void Dispose()
    {
        _searchCancel.Cancel();
        _preview.Dispose();
        Results.Clear();
    }
}
