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
using SoundBoardKing.Audio.MicFx;
using SoundBoardKing.Core.Import;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ProfileStorage _storage;
    private readonly AudioEngine _engine;
    private readonly ApoMicFeed _micFeed;
    private readonly HotkeyService _hotkeys;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _micTimer;
    private readonly Dictionary<Guid, SoundItemViewModel> _soundsById = [];

    private SoundLibrary _library = new();
    private AppSettings _settings = new();
    private CategoryNodeViewModel? _allSoundsNode;
    private bool _initialized;
    private bool _canSaveProfile = true;

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
    private bool isPlaying;

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private string micStatusText = "Microfono: …";

    [ObservableProperty]
    private bool isMicActive;

    public MainViewModel(
        ProfileStorage storage,
        AudioEngine engine,
        ApoMicFeed micFeed,
        HotkeyService hotkeys,
        IDialogService dialogs,
        UpdateService updates)
    {
        _storage = storage;
        _updates = updates;
        _engine = engine;
        _micFeed = micFeed;
        _hotkeys = hotkeys;
        _dialogs = dialogs;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _micTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _micTimer.Tick += (_, _) => UpdateMicStatus();

        for (int slot = 0; slot < Hotbar.SlotsPerPage; slot++)
            HotbarSlots.Add(new HotbarSlotViewModel(slot));

        _engine.PlaybackStarted += OnPlaybackStarted;
        _engine.PlaybackEnded += OnPlaybackEnded;
        _engine.OutputFailed += (_, ex) => StatusText = $"Dispositivo audio non disponibile: {ex.Message}";
    }

    public ObservableCollection<CategoryNodeViewModel> CategoryRoots { get; } = [];
    public ObservableCollection<HotbarSlotViewModel> HotbarSlots { get; } = [];

    /// <summary>Every sound playing right now, oldest first; each gets its own bar.</summary>
    public ObservableCollection<PlayingSoundViewModel> NowPlaying { get; } = [];

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

    /// <summary>Spectrum of what the speakers play, for the meter behind the volume slider.</summary>
    public Action<float[]> SpeakerSpectrum => _engine.ReadSpeakerSpectrum;

    /// <summary>Spectrum of only what goes into the microphone.</summary>
    public Action<float[]> MicSpectrum => _engine.ReadMicSpectrum;

    public double MicVolume
    {
        get => _settings.MicVolume * 100;
        set
        {
            var volume = (float)Math.Clamp(value / 100, 0, 1);
            if (Math.Abs(volume - _settings.MicVolume) < 0.0001f)
                return;
            _settings.MicVolume = volume;
            _engine.MicVolume = volume;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    /// <summary>The choices of the effect menu, with the current one ticked.</summary>
    public IReadOnlyList<EffectOptionViewModel> EffectOptions { get; } =
        Enum.GetValues<SoundEffect>().Select(e => new EffectOptionViewModel(e)).ToList();

    /// <summary>Voice effect for the sounds started from now on (the ones already playing keep theirs).</summary>
    public SoundEffect SoundEffect
    {
        get => _settings.SoundEffect;
        set
        {
            if (value == _settings.SoundEffect)
                return;
            _settings.SoundEffect = value;
            foreach (var option in EffectOptions)
                option.IsSelected = option.Effect == value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SoundEffectLabel));
            OnPropertyChanged(nameof(HasEffect));
            PushVoiceEffect();
            EffectChanged?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
    }

    public string SoundEffectLabel => EffectOptionViewModel.LabelOf(SoundEffect);

    public bool HasEffect => SoundEffect != SoundEffect.None;

    /// <summary>Sounds only, voice only, both or nothing; the button steps through them.</summary>
    public EffectTarget EffectTarget
    {
        get => _settings.EffectTarget;
        set
        {
            if (value == _settings.EffectTarget)
                return;
            _settings.EffectTarget = value;
            ApplyEffectTarget();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectTargetLabel));
            EffectChanged?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
    }

    public string EffectTargetLabel => EffectTarget switch
    {
        EffectTarget.Voice => "Solo microfono",
        EffectTarget.Both => "Suoni e microfono",
        EffectTarget.Off => "Nessuno",
        _ => "Solo suoni",
    };

    private bool EffectOnSounds => EffectTarget is EffectTarget.Sounds or EffectTarget.Both;
    private bool EffectOnVoice => EffectTarget is EffectTarget.Voice or EffectTarget.Both;

    [RelayCommand]
    private void CycleEffectTarget() => EffectTarget = EffectTarget switch
    {
        EffectTarget.Sounds => EffectTarget.Voice,
        EffectTarget.Voice => EffectTarget.Both,
        EffectTarget.Both => EffectTarget.Off,
        _ => EffectTarget.Sounds,
    };

    private void ApplyEffectTarget()
    {
        _engine.EffectControl.SoundsEnabled = EffectOnSounds;
        PushVoiceEffect();
    }

    // Also the heartbeat the mic module waits for: called every second by the mic timer.
    private void PushVoiceEffect() =>
        _micFeed.SetVoiceEffect(EffectOnVoice ? (int)SoundEffect : 0, _settings.EffectAmount);

    /// <summary>Effect strength 0-100 for the knob; also changes the sounds already playing.</summary>
    public double EffectAmount
    {
        get => _settings.EffectAmount * 100;
        set
        {
            float amount = (float)Math.Clamp(value / 100, 0, 1);
            if (amount == _settings.EffectAmount)
                return;
            _settings.EffectAmount = amount;
            _engine.EffectControl.Amount = amount;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectAmountPercent));
            PushVoiceEffect();
            EffectChanged?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
    }

    public int EffectAmountPercent => (int)Math.Round(_settings.EffectAmount * 100);

    /// <summary>
    /// Pad height, -100 (half speed) .. 0 (normal) .. 100 (double speed), for every sound, the
    /// ones playing too. Not saved: the next start always plays at normal speed.
    /// </summary>
    public double PadSpeed
    {
        get => Math.Log2(_engine.EffectControl.Speed) * 100;
        set
        {
            float speed = (float)Math.Pow(2, Math.Clamp(value, -100, 100) / 100);
            if (Math.Abs(speed - _engine.EffectControl.Speed) < 0.0005f)
                return;
            _engine.EffectControl.Speed = speed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedText));
            EffectChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public float PlaybackSpeed => _engine.EffectControl.Speed;

    public string SpeedText => $"×{_engine.EffectControl.Speed:0.00}";

    /// <summary>Back to normal speed and the default effect strength.</summary>
    [RelayCommand]
    private void ResetPad()
    {
        EffectAmount = EffectControl.DefaultAmount * 100;
        PadSpeed = 0;
    }

    [RelayCommand]
    private void SetSoundEffect(EffectOptionViewModel? option)
    {
        if (option is not null)
            SoundEffect = option.Effect;
    }

    /// <summary>Whether sounds are also sent into the microphone (when the module is active).</summary>
    public bool PlayOnMic
    {
        get => _settings.PlayOnMic;
        set
        {
            if (value == _settings.PlayOnMic)
                return;
            _settings.PlayOnMic = value;
            OnPropertyChanged();
            UpdateMicStatus();
            ScheduleSave();
        }
    }

    public void Initialize()
    {
        LoadProfile();

        _engine.SpeakerVolume = _settings.SpeakerVolume;
        _engine.MicVolume = _settings.MicVolume;
        _engine.SetOutputDevice(_storage.Local.OutputDeviceId);
        HotbarPage = Math.Clamp(_settings.HotbarPage, 0, Hotbar.PageCount - 1);
        OnPropertyChanged(nameof(SpeakerVolume));
        OnPropertyChanged(nameof(MicVolume));
        OnPropertyChanged(nameof(PlayOnMic));
        foreach (var option in EffectOptions)
            option.IsSelected = option.Effect == _settings.SoundEffect;
        OnPropertyChanged(nameof(SoundEffect));
        OnPropertyChanged(nameof(SoundEffectLabel));
        _engine.EffectControl.Amount = _settings.EffectAmount;
        OnPropertyChanged(nameof(EffectAmount));
        OnPropertyChanged(nameof(EffectAmountPercent));
        OnPropertyChanged(nameof(HasEffect));
        ApplyEffectTarget();
        OnPropertyChanged(nameof(EffectTarget));
        OnPropertyChanged(nameof(EffectTargetLabel));
        UpdateMicStatus();
        _micTimer.Start();

        RebuildAll();
        RegisterHotkeys();
        _initialized = true;

        if (_library.Sounds.Count == 0 && StatusText.Length == 0)
            StatusText = "Libreria vuota: importa la lista di SoundPad o trascina qui dei file audio.";
    }

    // ---- Playback ----------------------------------------------------------------------------

    [RelayCommand]
    private void PlaySound(SoundItemViewModel? sound)
    {
        sound ??= SelectedSound;
        if (sound is not null)
            TryPlay(sound);
    }

    /// <summary>Preview: only in the headphones, never in the microphone.</summary>
    [RelayCommand]
    private void PreviewSound(SoundItemViewModel? sound)
    {
        sound ??= SelectedSound;
        if (sound is not null)
            TryPlay(sound, PlayTarget.Speakers);
    }

    [RelayCommand]
    private void PlaySoundOnMic(SoundItemViewModel? sound)
    {
        sound ??= SelectedSound;
        if (sound is not null)
            TryPlay(sound, PlayTarget.Mic);
    }

    /// <summary>Returns why the sound could not start (also shown in the status bar), or null.</summary>
    private string? TryPlay(SoundItemViewModel sound, PlayTarget target = PlayTarget.Default)
    {
        if (target == PlayTarget.Mic && !_engine.IsMicAvailable)
            return StatusText = "Microfono non disponibile: installa il modulo dalle impostazioni.";
        try
        {
            _engine.Play(sound.Model.Path, new PlayRequest(
                sound.Id,
                sound.Model.Volume,
                Speakers: target != PlayTarget.Mic,
                Mic: target switch
                {
                    PlayTarget.Speakers => false,
                    PlayTarget.Mic => true,
                    _ => _settings.PlayOnMic && _engine.IsMicAvailable,
                },
                AllowOverlap: _settings.AllowOverlap,
                Effect: EffectOnSounds ? _settings.SoundEffect : SoundEffect.None));
            return null;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            sound.RefreshFileState();
            OnPropertyChanged(nameof(LibrarySummary));
            StatusText = sound.IsMissing
                ? $"File non trovato: {sound.Model.Path}"
                : $"Impossibile riprodurre \"{sound.Title}\": {ex.Message}";
            return StatusText;
        }
    }

    [RelayCommand]
    private void Stop() => _engine.StopAll();

    [RelayCommand]
    private void TogglePause()
    {
        _engine.TogglePause();
        PauseChanged();
    }

    /// <summary>Pauses or resumes one of the sounds in the playback bar.</summary>
    [RelayCommand]
    private void PauseOne(PlayingSoundViewModel? item)
    {
        if (item is null)
            return;
        _engine.TogglePause(item.Playback);
        PauseChanged();
    }

    private void PauseChanged()
    {
        foreach (var item in NowPlaying)
            item.Refresh();
        IsPaused = _engine.IsPaused;
        RaisePlaybackChanged();
    }

    /// <summary>Stops one of the sounds in the playback bar.</summary>
    [RelayCommand]
    private void StopOne(PlayingSoundViewModel? item)
    {
        if (item is not null)
            _engine.Stop(item.Playback);
    }

    private void OnPlaybackStarted(object? sender, Playback playback)
    {
        var sound = playback.Tag is Guid id ? _soundsById.GetValueOrDefault(id) : null;
        if (sound is not null)
            sound.IsPlaying = true;

        var item = new PlayingSoundViewModel(playback, sound?.Title ?? "");
        item.Refresh();
        NowPlaying.Add(item);
        UpdateNowPlaying();
    }

    private void OnPlaybackEnded(object? sender, Playback playback)
    {
        // The same sound may already have been restarted, so check before clearing the marker.
        if (playback.Tag is Guid id && _soundsById.TryGetValue(id, out var sound) && !_engine.IsPlayingTag(id))
            sound.IsPlaying = false;

        if (NowPlaying.FirstOrDefault(p => p.Playback == playback) is { } item)
            NowPlaying.Remove(item);
        UpdateNowPlaying();
    }

    private void UpdateNowPlaying()
    {
        IsPlaying = NowPlaying.Count > 0;
        IsPaused = _engine.IsPaused;
        if (IsPlaying)
            _progressTimer.Start();
        else
            _progressTimer.Stop();
        RaisePlaybackChanged();
    }

    private void UpdateProgress()
    {
        foreach (var item in NowPlaying)
            item.Refresh();
    }

    private void UpdateMicStatus()
    {
        PushVoiceEffect();
        var status = _micFeed.GetStatus();
        if (status.State != _micState || PlayOnMic != _micReportedEnabled)
        {
            _micState = status.State;
            _micReportedEnabled = PlayOnMic;
            MicChanged?.Invoke(this, CurrentMic());
        }
        IsMicActive = PlayOnMic && status.State == MicFxState.Active;
        MicStatusText = !PlayOnMic
            ? "Microfono: spento"
            : status.State switch
            {
                MicFxState.Active => "Microfono: attivo",
                MicFxState.Idle => "Microfono: in attesa",
                MicFxState.Incompatible => "Microfono: reinstalla il modulo",
                MicFxState.AccessDenied => "Microfono: accesso negato",
                _ => "Microfono: non installato",
            };
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
    private void PreviewHotbarSlot(HotbarSlotViewModel? slot)
    {
        if (slot?.Sound is { } sound)
            TryPlay(sound, PlayTarget.Speakers);
    }

    [RelayCommand]
    private void PlayHotbarSlotOnMic(HotbarSlotViewModel? slot)
    {
        if (slot?.Sound is { } sound)
            TryPlay(sound, PlayTarget.Mic);
    }

    [RelayCommand]
    private void ClearHotbarSlot(HotbarSlotViewModel? slot)
    {
        if (slot is null)
            return;
        _library.Hotbar.Set(HotbarPage, slot.Slot, null);
        slot.Sound = null;
        LibraryEdited();
    }

    public void AssignHotbarSlot(HotbarSlotViewModel slot, SoundItemViewModel sound)
    {
        _library.Hotbar.Set(HotbarPage, slot.Slot, sound.Id);
        slot.Sound = sound;
        LibraryEdited();
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
        var path = _dialogs.OpenSplFile(_storage.Local.LastImportFolder);
        if (path is null)
            return;
        if (_library.Sounds.Count > 0 &&
            !_dialogs.Confirm($"La libreria attuale ({_library.Sounds.Count} suoni) verrà sostituita dalla lista importata. Continuare?"))
            return;

        StatusText = "Importazione in corso… 0%";
        bool importing = true;
        var progress = new Progress<int>(percent =>
        {
            // Reports arrive asynchronously, so ignore any that land after the import finished.
            if (importing)
                StatusText = $"Importazione in corso… {percent}%";
        });
        SplImportResult result;
        try
        {
            result = await Task.Run(() => SplImporter.Import(path, progress));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
        {
            importing = false;
            StatusText = "";
            _dialogs.ShowError($"Importazione non riuscita: {ex.Message}");
            return;
        }
        importing = false;

        _engine.StopAll();
        _library = result.Library;
        _storage.Local.LastImportFolder = Path.GetDirectoryName(path);
        RebuildAll();
        RegisterHotkeys();
        SaveNow();
        NotifyLibraryChanged();

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
        LibraryEdited();
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
        LibraryEdited();
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
        LibraryEdited();
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
        LibraryEdited();
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
        LibraryEdited();
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
        LibraryEdited();
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
        LibraryEdited();
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
        LibraryEdited();
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
        LibraryEdited();
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

        var model = new SettingsDialogModel(devices, this, this)
        {
            SelectedDevice = devices.FirstOrDefault(d => d.Id == (_storage.Local.OutputDeviceId ?? "")) ?? devices[0],
            AllowOverlap = _settings.AllowOverlap,
            StopHotkey = _settings.StopHotkey,
            PauseHotkey = _settings.PauseHotkey,
        };
        bool accepted = _dialogs.EditSettings(model);
        UpdateMicStatus(); // the module may have been installed or removed from the dialog
        if (model.RestartRequested)
        {
            RestartRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!accepted)
            return;

        var deviceId = string.IsNullOrEmpty(model.SelectedDevice?.Id) ? null : model.SelectedDevice.Id;
        if (deviceId != _storage.Local.OutputDeviceId)
        {
            _storage.Local.OutputDeviceId = deviceId;
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
        if (model.UpdateRestartRequested)
            RestartToUpdate(); // "Riavvia e aggiorna" also confirms the dialog
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
            _storage.SaveLocal();
            if (_canSaveProfile)
                _storage.Save(CurrentProfile());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Salvataggio non riuscito: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _progressTimer.Stop();
        _micTimer.Stop();
        if (_initialized)
            SaveNow();
        _micFeed.SetVoiceEffect(0, 0); // the voice goes back to normal at once, not after the timeout
        _hotkeys.Dispose();
        _engine.Dispose();
        ApplyDownloadedUpdate();
    }
}
