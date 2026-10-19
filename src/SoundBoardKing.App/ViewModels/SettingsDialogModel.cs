using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.App.Services;
using SoundBoardKing.Audio;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.ViewModels;

public sealed record MicDeviceItem(string Id, string Name, string? InstalledSlot)
{
    public string Display => InstalledSlot is null ? Name : $"{Name}  —  installato ({InstalledSlot.ToUpperInvariant()})";
}

public sealed record MicSlotOption(string Value, string Label);

public sealed partial class SettingsDialogModel : ObservableObject
{
    [ObservableProperty]
    private AudioDevice? selectedDevice;

    [ObservableProperty]
    private bool allowOverlap;

    [ObservableProperty]
    private bool levelSounds;

    [ObservableProperty]
    private string? stopHotkey;

    [ObservableProperty]
    private string? pauseHotkey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallMicCommand), nameof(UninstallMicCommand))]
    private MicDeviceItem? selectedMicDevice;

    [ObservableProperty]
    private MicSlotOption selectedMicSlot = null!;

    [ObservableProperty]
    private IReadOnlyList<MicDeviceItem> micDevices = [];

    [ObservableProperty]
    private string micLog = "";

    [ObservableProperty]
    private string profilePath = "";

    [ObservableProperty]
    private string profileLog = "";

    [ObservableProperty]
    private string updateLog = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool isCheckingForUpdates;

    private readonly IProfileActions _profile;
    private readonly IUpdateActions _updates;

    public SettingsDialogModel(IReadOnlyList<AudioDevice> devices, IProfileActions profile, IUpdateActions updates)
    {
        Devices = devices;
        _profile = profile;
        _updates = updates;
        UpdateLog = updates.UpdateReadyVersion is { } ready
            ? $"Versione {ready} scaricata: si installa alla chiusura, o subito con \"Riavvia e aggiorna\"."
            : updates.CanUpdate ? "" : "Questa copia non si aggiorna da sola (cartella di sviluppo o pacchetto senza installer).";
        ProfilePath = profile.ProfilePath;
        SelectedMicSlot = MicSlots[0];
        RefreshMicDevices(null);
    }

    /// <summary>Device with an empty id stands for "follow the Windows default".</summary>
    public IReadOnlyList<AudioDevice> Devices { get; }

    public IReadOnlyList<MicSlotOption> MicSlots { get; } =
    [
        new("auto", "Automatica — consigliata"),
        new("lfx", "Classica, prima del mix (LFX)"),
        new("gfx", "Classica, dopo il mix (GFX)"),
        new("sfx", "Per applicazione (SFX)"),
        new("mfx", "Per modalità (MFX)"),
        new("efx", "Endpoint (EFX)"),
    ];

    public bool IsMicSetupAvailable => MicModuleService.IsSetupAvailable;

    private bool CanInstallMic() => SelectedMicDevice is not null && IsMicSetupAvailable;

    private bool CanUninstallMic() => SelectedMicDevice?.InstalledSlot is not null && IsMicSetupAvailable;

    [RelayCommand(CanExecute = nameof(CanInstallMic))]
    private async Task InstallMicAsync()
    {
        var device = SelectedMicDevice!;
        MicLog = $"Installazione su \"{device.Name}\"… conferma la richiesta di amministratore.";
        var result = await MicModuleService.InstallAsync(device.Id, SelectedMicSlot.Value);
        MicLog = (result.Success ? "Fatto. " : "Non riuscito. ") + result.Message;
        RefreshMicDevices(device.Id);
    }

    [RelayCommand(CanExecute = nameof(CanUninstallMic))]
    private async Task UninstallMicAsync()
    {
        var device = SelectedMicDevice!;
        MicLog = $"Rimozione da \"{device.Name}\"… conferma la richiesta di amministratore.";
        var result = await MicModuleService.UninstallAsync(device.Id);
        MicLog = (result.Success ? "Fatto. " : "Non riuscito. ") + result.Message;
        RefreshMicDevices(device.Id);
    }

    // ---- Version and updates ----------------------------------------------------------------

    public string AppVersion => _updates.AppVersion;

    public bool IsUpdateReady => _updates.UpdateReadyVersion is not null;

    /// <summary>Set when the user chose to install the downloaded update now.</summary>
    public bool UpdateRestartRequested { get; private set; }

    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateLog = "Controllo su GitHub…";
        try
        {
            UpdateLog = await _updates.CheckForUpdatesAsync();
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
        OnPropertyChanged(nameof(IsUpdateReady));
    }

    public void RequestUpdateRestart() => UpdateRestartRequested = true;

    // ---- Sounds folder ----------------------------------------------------------------------

    /// <summary>As written in the settings: relative to the app (e.g. ..\Suoni) or a fixed path; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoundsFolderInfo), nameof(IsSoundsFolderRelative), nameof(CanSoundsFolderBeRelative))]
    private string soundsFolder = "";

    public string AppFolder => _profile.AppFolder;

    private string? ResolvedSoundsFolder => SoundFolder.Resolve(SoundsFolder, AppFolder);

    public string SoundsFolderInfo => ResolvedSoundsFolder is { } full
        ? (IsSoundsFolderRelative ? $"Relativo all'app: {full}" : full)
        : SoundsFolder.Trim().Length == 0 ? "Nessuna cartella: i suoni restano dove sono." : "Percorso non valido.";

    public bool CanSoundsFolderBeRelative =>
        ResolvedSoundsFolder is { } full && SoundFolder.RelativeToApp(full, AppFolder) is not null;

    /// <summary>Switches the same folder between relative to the app and fixed path.</summary>
    public bool IsSoundsFolderRelative
    {
        get => SoundsFolder.Trim().Length > 0 && !Path.IsPathRooted(SoundsFolder.Trim());
        set
        {
            if (value == IsSoundsFolderRelative || ResolvedSoundsFolder is not { } full)
                return;
            SoundsFolder = value ? SoundFolder.RelativeToApp(full, AppFolder) ?? full : full;
        }
    }

    /// <summary>
    /// A folder picked with "Sfoglia…": kept relative when the current one is (or nothing was chosen
    /// yet and it sits next to the app), otherwise as a fixed path.
    /// </summary>
    public void ChooseSoundsFolder(string fullPath)
    {
        var relative = SoundFolder.RelativeToApp(fullPath, AppFolder);
        bool nearApp = relative is not null && !relative.StartsWith(@"..\..", StringComparison.Ordinal);
        bool keepRelative = SoundsFolder.Trim().Length == 0 ? nearApp : IsSoundsFolderRelative;
        SoundsFolder = keepRelative && relative is not null ? relative : fullPath;
    }

    // ---- Profile ----------------------------------------------------------------------------

    public string ExecutableProfilePath => _profile.ExecutableProfilePath;

    public string ProfileLocationText => _profile.ProfileLocationText;

    public bool AllowsAppFolder => _profile.AllowsAppFolder;

    public bool CanMoveNextToExecutable => AllowsAppFolder && !IsProfileNextToExecutable;

    public bool IsProfileNextToExecutable =>
        string.Equals(ProfilePath, ExecutableProfilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Set when another profile was opened: the app restarts to load it.</summary>
    public bool RestartRequested { get; private set; }

    partial void OnProfilePathChanged(string value)
    {
        OnPropertyChanged(nameof(IsProfileNextToExecutable));
        OnPropertyChanged(nameof(CanMoveNextToExecutable));
        OnPropertyChanged(nameof(ProfileLocationText));
    }

    public void MoveProfile(string path)
    {
        var error = _profile.MoveProfile(path);
        ProfilePath = _profile.ProfilePath;
        ProfileLog = error is null ? "Profilo spostato: da ora le modifiche vengono salvate qui." : "Non riuscito. " + error;
    }

    public void ExportProfile(string path)
    {
        var error = _profile.ExportProfile(path);
        ProfileLog = error is null ? $"Copia salvata in {path}." : "Non riuscito. " + error;
    }

    /// <summary>Returns true when the app is about to restart with the chosen profile.</summary>
    public bool UseProfile(string path)
    {
        var error = _profile.UseProfile(path);
        if (error is not null)
        {
            ProfileLog = "Non riuscito. " + error;
            return false;
        }
        RestartRequested = true;
        return true;
    }

    private void RefreshMicDevices(string? selectId)
    {
        var installed = MicModuleService.InstalledDevices();
        IReadOnlyList<AudioDevice> capture;
        try
        {
            capture = AudioEngine.GetCaptureDevices();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            capture = [];
        }

        MicDevices = capture
            .Select(d => new MicDeviceItem(d.Id, d.Name, installed.GetValueOrDefault(d.Id)))
            .ToList();
        SelectedMicDevice = MicDevices.FirstOrDefault(d => d.Id == selectId)
            ?? MicDevices.FirstOrDefault(d => d.InstalledSlot is not null)
            ?? MicDevices.FirstOrDefault();

        if (MicDevices.Count == 0)
            MicLog = "Nessun microfono trovato su questo PC.";
        else if (!IsMicSetupAvailable)
            MicLog = "Il modulo microfono non è incluso in questa build (manca SbkMicFx.dll: esegui build-native.ps1).";
    }
}
