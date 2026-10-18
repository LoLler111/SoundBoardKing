using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.App.Services;
using SoundBoardKing.Audio;

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

    private readonly IProfileActions _profile;

    public SettingsDialogModel(IReadOnlyList<AudioDevice> devices, IProfileActions profile)
    {
        Devices = devices;
        _profile = profile;
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

    // ---- Profile ----------------------------------------------------------------------------

    public string ExecutableProfilePath => _profile.ExecutableProfilePath;

    public bool CanMoveNextToExecutable => !IsProfileNextToExecutable;

    public bool IsProfileNextToExecutable =>
        string.Equals(ProfilePath, ExecutableProfilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Set when another profile was opened: the app restarts to load it.</summary>
    public bool RestartRequested { get; private set; }

    partial void OnProfilePathChanged(string value)
    {
        OnPropertyChanged(nameof(IsProfileNextToExecutable));
        OnPropertyChanged(nameof(CanMoveNextToExecutable));
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
