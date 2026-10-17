using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Xml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.App.Hotkeys;
using SoundBoardKing.App.Services;
using SoundBoardKing.Audio;
using SoundBoardKing.Core.Import;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string IdleTitle = "Nessun suono in riproduzione";

    private readonly JsonFileStore<SoundLibrary> _libraryStore;
    private readonly JsonFileStore<AppSettings> _settingsStore;
    private readonly AudioEngine _engine;
    private readonly HotkeyService _hotkeys;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly Dictionary<Guid, SoundItemViewModel> _soundsById = [];

    private SoundLibrary _library = new();
    private AppSettings _settings = new();
    private CategoryNodeViewModel? _allSoundsNode;
    private bool _initialized;
    private bool _canSaveLibrary = true;
    private bool _canSaveSettings = true;

    [ObservableProperty]
    private IReadOnlyList<SoundItemViewModel> visibleSounds = [];

    [ObservableProperty]
    private SoundItemViewModel? selectedSound;

    [ObservableProperty]
    private CategoryNodeViewModel? selectedCategory;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private int hotbarPage;

    [ObservableProperty]
    private string nowPlayingTitle = IdleTitle;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private double durationSeconds = 1;

    [ObservableProperty]
    private string positionText = "";

    [ObservableProperty]
    private string statusText = "";

    public MainViewModel(
        JsonFileStore<SoundLibrary> libraryStore,
        JsonFileStore<AppSettings> settingsStore,
        AudioEngine engine,
        HotkeyService hotkeys,
        IDialogService dialogs)
    {
        _libraryStore = libraryStore;
        _settingsStore = settingsStore;
        _engine = engine;
        _hotkeys = hotkeys;
        _dialogs = dialogs;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        for (int slot = 0; slot < Hotbar.SlotsPerPage; slot++)
            HotbarSlots.Add(new HotbarSlotViewModel(slot));

        _engine.PlaybackStarted += OnPlaybackStarted;
        _engine.PlaybackEnded += OnPlaybackEnded;
        _engine.OutputFailed += (_, ex) => StatusText = $"Dispositivo audio non disponibile: {ex.Message}";
    }

    public ObservableCollection<CategoryNodeViewModel> CategoryRoots { get; } = [];
    public ObservableCollection<HotbarSlotViewModel> HotbarSlots { get; } = [];

    public string LibrarySummary
    {
        get
        {
            int missing = _soundsById.Values.Count(s => s.IsMissing);
            return missing == 0
                ? $"{_library.Sounds.Count} suoni"
                : $"{_library.Sounds.Count} suoni · {missing} file mancanti";
        }
    }

    public double SpeakerVolume
    {
        get => _settings.SpeakerVolume * 100;
        set
        {
            var volume = (float)Math.Clamp(value / 100, 0, 1);
            if (Math.Abs(volume - _settings.SpeakerVolume) < 0.0001f)
                return;
            _settings.SpeakerVolume = volume;
            _engine.SpeakerVolume = volume;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public bool IsMicAvailable => _engine.IsMicAvailable;

    public void Initialize()
    {
        (_settings, _canSaveSettings) = Load(_settingsStore);
        (_library, _canSaveLibrary) = Load(_libraryStore);

        _engine.SpeakerVolume = _settings.SpeakerVolume;
        _engine.SetOutputDevice(_settings.OutputDeviceId);
        HotbarPage = Math.Clamp(_settings.HotbarPage, 0, Hotbar.PageCount - 1);
        OnPropertyChanged(nameof(SpeakerVolume));

        RebuildAll();
        RegisterHotkeys();
        _initialized = true;

        if (_library.Sounds.Count == 0 && StatusText.Length == 0)
            StatusText = "Libreria vuota: importa la lista di SoundPad o trascina qui dei file audio.";
    }

    private (T Value, bool CanSave) Load<T>(JsonFileStore<T> store) where T : class, new()
    {
        try
        {
            return (store.Load(), true);
        }
        catch (InvalidDataException ex)
        {
            // The corrupt file was moved aside, so starting fresh cannot overwrite it.
            _dialogs.ShowError(ex.Message);
            return (new T(), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Impossibile leggere {store.FilePath}: {ex.Message}\nLe modifiche di questa sessione non verranno salvate.");
            return (new T(), false);
        }
    }

    // ---- Playback ----------------------------------------------------------------------------

    [RelayCommand]
    private void PlaySound(SoundItemViewModel? sound)
    {
        sound ??= SelectedSound;
        if (sound is null)
            return;

        try
        {
            _engine.Play(sound.Model.Path, new PlayRequest(
                sound.Id,
                sound.Model.Volume,
                Speakers: true,
                Mic: _settings.PlayOnMic && _engine.IsMicAvailable,
                AllowOverlap: _settings.AllowOverlap));
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            sound.RefreshFileState();
            OnPropertyChanged(nameof(LibrarySummary));
            StatusText = sound.IsMissing
                ? $"File non trovato: {sound.Model.Path}"
                : $"Impossibile riprodurre \"{sound.Title}\": {ex.Message}";
        }
    }

    [RelayCommand]
    private void Stop() => _engine.StopAll();

    [RelayCommand]
    private void TogglePause()
    {
        _engine.TogglePause();
        IsPaused = _engine.IsPaused;
    }

    private void OnPlaybackStarted(object? sender, Playback playback)
    {
        if (playback.Tag is Guid id && _soundsById.TryGetValue(id, out var sound))
            sound.IsPlaying = true;
        UpdateNowPlaying();
    }

    private void OnPlaybackEnded(object? sender, Playback playback)
    {
        // The same sound may already have been restarted, so check before clearing the marker.
        if (playback.Tag is Guid id && _soundsById.TryGetValue(id, out var sound) && !_engine.IsPlayingTag(id))
            sound.IsPlaying = false;
        UpdateNowPlaying();
    }

    private void UpdateNowPlaying()
    {
        var current = _engine.Current;
        IsPlaying = current is not null;
        IsPaused = _engine.IsPaused;
        if (current is null)
        {
            _progressTimer.Stop();
            NowPlayingTitle = IdleTitle;
            PositionSeconds = 0;
            PositionText = "";
            return;
        }

        NowPlayingTitle = current.Tag is Guid id && _soundsById.TryGetValue(id, out var sound) ? sound.Title : "";
        UpdateProgress();
        _progressTimer.Start();
    }

    private void UpdateProgress()
    {
        if (_engine.Current is not { } current)
            return;
        DurationSeconds = Math.Max(current.Duration.TotalSeconds, 0.001);
        PositionSeconds = Math.Min(current.Position.TotalSeconds, DurationSeconds);
        PositionText = $"{TimeFormat.Format(current.Position)} / {TimeFormat.Format(current.Duration)}";
    }

    // ---- Hotbar ------------------------------------------------------------------------------

    partial void OnHotbarPageChanged(int value)
    {
        RefreshHotbar();
        _settings.HotbarPage = value;
        ScheduleSave();
    }

    [RelayCommand]
    private void NextHotbarPage() => HotbarPage = (HotbarPage + 1) % Hotbar.PageCount;

    [RelayCommand]
    private void PreviousHotbarPage() => HotbarPage = (HotbarPage + Hotbar.PageCount - 1) % Hotbar.PageCount;

    [RelayCommand]
    private void PlayHotbarSlot(HotbarSlotViewModel? slot)
    {
        if (slot?.Sound is { } sound)
            PlaySound(sound);
    }

    [RelayCommand]
    private void ClearHotbarSlot(HotbarSlotViewModel? slot)
    {
        if (slot is null)
            return;
        _library.Hotbar.Set(HotbarPage, slot.Slot, null);
        slot.Sound = null;
        ScheduleSave();
    }

    public void AssignHotbarSlot(HotbarSlotViewModel slot, SoundItemViewModel sound)
    {
        _library.Hotbar.Set(HotbarPage, slot.Slot, sound.Id);
        slot.Sound = sound;
        ScheduleSave();
    }

    [RelayCommand]
    private void AddToHotbar(SoundItemViewModel? sound)
    {
        sound ??= SelectedSound;
        if (sound is null)
            return;
        var free = HotbarSlots.FirstOrDefault(s => s.IsEmpty);
        if (free is null)
        {
            StatusText = $"La pagina {HotbarPage + 1} dei preferiti è piena: trascina il suono su uno slot per sostituirlo.";
            return;
        }
        AssignHotbarSlot(free, sound);
        StatusText = $"\"{sound.Title}\" aggiunto ai preferiti (pagina {HotbarPage + 1}, slot {free.Number}).";
    }

    private void RefreshHotbar()
    {
        foreach (var slot in HotbarSlots)
        {
            var id = _library.Hotbar.Get(HotbarPage, slot.Slot);
            slot.Sound = id is Guid soundId ? _soundsById.GetValueOrDefault(soundId) : null;
        }
    }

    // ---- Library -----------------------------------------------------------------------------

    [RelayCommand]
    private async Task ImportSplAsync()
    {
        var path = _dialogs.OpenSplFile(_settings.LastImportFolder);
        if (path is null)
            return;
        if (_library.Sounds.Count > 0 &&
            !_dialogs.Confirm($"La libreria attuale ({_library.Sounds.Count} suoni) verrà sostituita dalla lista importata. Continuare?"))
            return;

        StatusText = "Importazione in corso…";
        SplImportResult result;
        try
        {
            result = await Task.Run(() => SplImporter.Import(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
        {
            StatusText = "";
            _dialogs.ShowError($"Importazione non riuscita: {ex.Message}");
            return;
        }

        _engine.StopAll();
        _library = result.Library;
        _settings.LastImportFolder = Path.GetDirectoryName(path);
        RebuildAll();
        RegisterHotkeys();
        SaveNow();

        var message = $"Importati {_library.Sounds.Count} suoni, {_library.EnumerateCategories().Count()} categorie " +
                      $"e {_library.Hotbar.Slots.Count} preferiti.";
        if (result.MissingFiles > 0)
            message += $"\n\n{result.MissingFiles} file non trovati: sono evidenziati in rosso nella lista.";
        if (result.Warnings.Count > 0)
            message += $"\n\nAvvisi:\n{string.Join("\n", result.Warnings.Take(8))}";
        StatusText = $"Importati {_library.Sounds.Count} suoni da {Path.GetFileName(path)}.";
        _dialogs.ShowInfo(message);
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = _dialogs.OpenAudioFiles();
        if (files.Count > 0)
            await AddPathsAsync(files);
    }

    /// <summary>Adds audio files (folders are scanned recursively) to the library and the selected category.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var known = _library.Sounds.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = await Task.Run(() => ExpandAudioFiles(paths)
            .Where(f => !known.Contains(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList());
        if (files.Count == 0)
        {
            StatusText = "Nessun nuovo file audio da aggiungere.";
            return;
        }

        StatusText = $"Aggiunta di {files.Count} suoni…";
        var sounds = await Task.Run(() => files.Select(file => new Sound
        {
            Title = Path.GetFileNameWithoutExtension(file),
            Path = file,
            DurationMs = (long)(AudioFileProbe.TryGetDuration(file)?.TotalMilliseconds ?? 0),
        }).ToList());

        var target = SelectedCategory?.Model;
        foreach (var sound in sounds)
        {
            _library.Sounds.Add(sound);
            target?.SoundIds.Add(sound.Id);
            _soundsById[sound.Id] = new SoundItemViewModel(sound);
        }

        RefreshCounts();
        RefreshVisible();
        ScheduleSave();
        StatusText = target is null
            ? $"Aggiunti {sounds.Count} suoni."
            : $"Aggiunti {sounds.Count} suoni alla categoria \"{target.Name}\".";
    }

    private static IEnumerable<string> ExpandAudioFiles(IEnumerable<string> paths)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", options).Where(AudioFileProbe.IsSupported))
                    yield return file;
            }
            else if (File.Exists(path) && AudioFileProbe.IsSupported(path))
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

    [RelayCommand]
    private void RemoveSounds(IList? selection)
    {
        var sounds = GetSelection(selection);
        if (sounds.Count == 0)
            return;
        var question = sounds.Count == 1
            ? $"Rimuovere \"{sounds[0].Title}\" dalla libreria?"
            : $"Rimuovere {sounds.Count} suoni dalla libreria?";
        if (!_dialogs.Confirm(question + "\nI file audio sul disco non vengono cancellati."))
            return;

        var ids = sounds.Select(s => s.Id).ToHashSet();
        _library.RemoveSounds(ids);
        foreach (var id in ids)
            _soundsById.Remove(id);

        RefreshCounts();
        RefreshVisible();
        RefreshHotbar();
        RegisterHotkeys();
        ScheduleSave();
        StatusText = $"Rimossi {ids.Count} suoni.";
    }

    [RelayCommand]
    private void RenameSound(SoundItemViewModel? sound)
    {
        sound ??= SelectedSound;
        if (sound is null)
            return;
        var title = _dialogs.AskText("Rinomina suono", "Titolo:", sound.Title)?.Trim();
        if (string.IsNullOrEmpty(title) || title == sound.Title)
            return;
        sound.Title = title;
        ScheduleSave();
    }

    [RelayCommand]
    private void OpenFileLocation(SoundItemViewModel? sound)
    {
        sound ??= SelectedSound;
        if (sound is null)
            return;
        if (!File.Exists(sound.Model.Path))
        {
            StatusText = $"File non trovato: {sound.Model.Path}";
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{sound.Model.Path}\"") { UseShellExecute = true });
    }

    private List<SoundItemViewModel> GetSelection(IList? selection)
    {
        var sounds = selection?.OfType<SoundItemViewModel>().ToList() ?? [];
        if (sounds.Count == 0 && SelectedSound is not null)
            sounds.Add(SelectedSound);
        return sounds;
    }

    // ---- Categories --------------------------------------------------------------------------

    partial void OnSelectedCategoryChanged(CategoryNodeViewModel? value) => RefreshVisible();

    partial void OnSearchTextChanged(string value) => RefreshVisible();

    [RelayCommand]
    private void NewCategory() => CreateCategory(null);

    [RelayCommand]
    private void NewSubcategory() => CreateCategory(SelectedCategory?.Model);

    private void CreateCategory(Category? parent)
    {
        var name = _dialogs.AskText(
            parent is null ? "Nuova categoria" : $"Nuova sottocategoria di \"{parent.Name}\"",
            "Nome della categoria:")?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        var category = new Category { Name = name, Icon = name[..1].ToUpperInvariant() };
        (parent?.Children ?? _library.Categories).Add(category);
        RebuildCategories(select: category);
        ScheduleSave();
    }

    [RelayCommand]
    private void RenameCategory()
    {
        if (SelectedCategory is not { Model: { } category } node)
            return;
        var name = _dialogs.AskText("Rinomina categoria", "Nome della categoria:", category.Name)?.Trim();
        if (string.IsNullOrEmpty(name) || name == category.Name)
            return;
        category.Name = name;
        node.NotifyNameChanged();
        ScheduleSave();
    }

    [RelayCommand]
    private void DeleteCategory()
    {
        if (SelectedCategory?.Model is not { } category)
            return;
        if (!_dialogs.Confirm($"Eliminare la categoria \"{category.Name}\" e le sue sottocategorie?\nI suoni restano nella libreria."))
            return;
        _library.RemoveCategory(category);
        RebuildCategories(select: null);
        ScheduleSave();
    }

    public void AddSoundsToCategory(CategoryNodeViewModel node, IReadOnlyList<SoundItemViewModel> sounds)
    {
        if (node.Model is not { } category)
            return;
        int added = 0;
        foreach (var sound in sounds.Where(s => !category.SoundIds.Contains(s.Id)))
        {
            category.SoundIds.Add(sound.Id);
            added++;
        }
        if (added == 0)
            return;

        RefreshCounts();
        if (node == SelectedCategory)
            RefreshVisible();
        ScheduleSave();
        StatusText = $"{added} suoni aggiunti a \"{category.Name}\".";
    }

    [RelayCommand]
    private void RemoveFromCategory(IList? selection)
    {
        if (SelectedCategory?.Model is not { } category)
        {
            StatusText = "Seleziona prima una categoria.";
            return;
        }
        var ids = GetSelection(selection).Select(s => s.Id).ToHashSet();
        if (category.SoundIds.RemoveAll(ids.Contains) == 0)
            return;
        RefreshCounts();
        RefreshVisible();
        ScheduleSave();
    }

    private void RebuildAll()
    {
        _soundsById.Clear();
        foreach (var sound in _library.Sounds)
            _soundsById[sound.Id] = new SoundItemViewModel(sound);

        RebuildCategories(select: SelectedCategory?.Model);
        RefreshVisible();
        RefreshHotbar();
        OnPropertyChanged(nameof(LibrarySummary));
    }

    private void RebuildCategories(Category? select)
    {
        CategoryRoots.Clear();
        _allSoundsNode = new CategoryNodeViewModel(null, null, OnCategoryNodeSelected) { IsExpanded = true };
        CategoryRoots.Add(_allSoundsNode);
        foreach (var category in _library.Categories)
            CategoryRoots.Add(BuildNode(category, null));
        RefreshCounts();

        var target = select is null
            ? null
            : CategoryRoots.SelectMany(n => n.SelfAndDescendants()).FirstOrDefault(n => n.Model == select);
        for (var ancestor = target?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            ancestor.IsExpanded = true;

        var node = target ?? _allSoundsNode;
        node.IsSelected = true;
        SelectedCategory = node;
    }

    private CategoryNodeViewModel BuildNode(Category category, CategoryNodeViewModel? parent)
    {
        var node = new CategoryNodeViewModel(category, parent, OnCategoryNodeSelected);
        foreach (var child in category.Children)
            node.Children.Add(BuildNode(child, node));
        return node;
    }

    private void OnCategoryNodeSelected(CategoryNodeViewModel node) => SelectedCategory = node;

    private void RefreshCounts()
    {
        foreach (var node in CategoryRoots.SelectMany(n => n.SelfAndDescendants()))
            node.Count = node.Model?.SoundIds.Count ?? _library.Sounds.Count;
        OnPropertyChanged(nameof(LibrarySummary));
    }

    private void RefreshVisible()
    {
        IEnumerable<SoundItemViewModel> sounds = SelectedCategory?.Model is { } category
            ? category.SoundIds.Select(id => _soundsById.GetValueOrDefault(id)).OfType<SoundItemViewModel>()
            : _library.Sounds.Select(s => _soundsById[s.Id]);

        var query = SearchText.Trim();
        if (query.Length > 0)
        {
            sounds = sounds.Where(s =>
                s.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                s.Artist.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }
        VisibleSounds = sounds.ToList();
    }

    // ---- Hotkeys and settings ----------------------------------------------------------------

    [RelayCommand]
    private void AssignHotkey(SoundItemViewModel? sound)
    {
        sound ??= SelectedSound;
        if (sound is null)
            return;
        var result = _dialogs.CaptureHotkey($"Tasto rapido per \"{sound.Title}\"", sound.Hotkey);
        if (result is null)
            return;

        var hotkey = result.Length == 0 ? null : result;
        if (hotkey is not null)
            ReleaseHotkey(hotkey, except: sound);
        sound.Hotkey = hotkey;
        RegisterHotkeys();
        ScheduleSave();
    }

    /// <summary>A combination can do only one thing: take it away from whoever had it.</summary>
    private void ReleaseHotkey(string hotkey, SoundItemViewModel? except)
    {
        foreach (var sound in _soundsById.Values)
        {
            if (sound != except && string.Equals(sound.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                sound.Hotkey = null;
        }
        if (string.Equals(_settings.StopHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
            _settings.StopHotkey = null;
        if (string.Equals(_settings.PauseHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
            _settings.PauseHotkey = null;
    }

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var failed = new List<string>();

        void Register(string? text, Action action, string label)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            if (!HotkeyGesture.TryParse(text, out var gesture) || !_hotkeys.Register(gesture, action))
                failed.Add($"{text} ({label})");
        }

        Register(_settings.StopHotkey, Stop, "ferma");
        Register(_settings.PauseHotkey, TogglePause, "pausa");
        foreach (var sound in _soundsById.Values)
            Register(sound.Hotkey, () => PlaySound(sound), sound.Title);

        if (failed.Count > 0)
            StatusText = "Tasti rapidi non disponibili (forse usati da un'altra app): " + string.Join(", ", failed);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var devices = new List<AudioDevice> { new("", "Predefinito di Windows") };
        try
        {
            devices.AddRange(AudioEngine.GetOutputDevices());
        }
        catch (COMException ex)
        {
            StatusText = $"Impossibile elencare i dispositivi audio: {ex.Message}";
        }

        var model = new SettingsDialogModel(devices)
        {
            SelectedDevice = devices.FirstOrDefault(d => d.Id == (_settings.OutputDeviceId ?? "")) ?? devices[0],
            AllowOverlap = _settings.AllowOverlap,
            StopHotkey = _settings.StopHotkey,
            PauseHotkey = _settings.PauseHotkey,
        };
        if (!_dialogs.EditSettings(model))
            return;

        var deviceId = string.IsNullOrEmpty(model.SelectedDevice?.Id) ? null : model.SelectedDevice.Id;
        if (deviceId != _settings.OutputDeviceId)
        {
            _settings.OutputDeviceId = deviceId;
            _engine.SetOutputDevice(deviceId);
        }
        _settings.AllowOverlap = model.AllowOverlap;

        var stop = model.StopHotkey;
        var pause = string.Equals(model.PauseHotkey, stop, StringComparison.OrdinalIgnoreCase) ? null : model.PauseHotkey;
        _settings.StopHotkey = null;
        _settings.PauseHotkey = null;
        if (stop is not null)
            ReleaseHotkey(stop, except: null);
        if (pause is not null)
            ReleaseHotkey(pause, except: null);
        _settings.StopHotkey = stop;
        _settings.PauseHotkey = pause;

        RegisterHotkeys();
        ScheduleSave();
    }

    // ---- Persistence -------------------------------------------------------------------------

    private void ScheduleSave()
    {
        if (!_initialized)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            if (_canSaveLibrary)
                _libraryStore.Save(_library);
            if (_canSaveSettings)
                _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Salvataggio non riuscito: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _progressTimer.Stop();
        if (_initialized)
            SaveNow();
        _hotkeys.Dispose();
        _engine.Dispose();
    }
}
