using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.App.Services;

namespace SoundBoardKing.App.ViewModels;

/// <summary>What the settings dialog shows and does about app updates.</summary>
public interface IUpdateActions
{
    string AppVersion { get; }

    /// <summary>False for a plain build folder: only Setup.exe and the Portable zip update themselves.</summary>
    bool CanUpdate { get; }

    string? UpdateReadyVersion { get; }

    /// <summary>Checks GitHub and downloads a newer version; returns what happened, in words.</summary>
    Task<string> CheckForUpdatesAsync();

    void RestartToUpdate();
}

// App updates from GitHub releases (Velopack) and the mic module that may come with them.
public sealed partial class MainViewModel : IUpdateActions
{
    private readonly UpdateService _updates;
    private bool _restartAfterUpdate;

    /// <summary>The window closes so the updater can replace the app and start it again.</summary>
    public event EventHandler? UpdateRestartRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateReady))]
    private string? updateReadyVersion;

    public bool IsUpdateReady => UpdateReadyVersion is not null;

    public string AppVersion => UpdateService.CurrentVersion;

    public bool CanUpdate => _updates.CanUpdate;

    /// <summary>Quietly looks for a newer release after start-up; a failure (offline) is ignored.</summary>
    public async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            UpdateReadyVersion = await _updates.DownloadLatestAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
        }
    }

    public async Task<string> CheckForUpdatesAsync()
    {
        if (!CanUpdate)
            return "Questa copia non si aggiorna da sola: installa SoundBoardKing con Setup.exe o usa lo zip Portable delle release.";
        try
        {
            UpdateReadyVersion = await _updates.DownloadLatestAsync();
            return UpdateReadyVersion is null
                ? $"Hai già l'ultima versione ({AppVersion})."
                : $"Versione {UpdateReadyVersion} scaricata: si installa alla chiusura, o subito con \"Riavvia e aggiorna\".";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return $"Controllo non riuscito: {ex.Message}";
        }
    }

    [RelayCommand]
    public void RestartToUpdate()
    {
        if (!IsUpdateReady)
            return;
        _restartAfterUpdate = true;
        UpdateRestartRequested?.Invoke(this, EventArgs.Empty);
    }

    // On exit (after the profile is saved), the updater takes over once this process is gone.
    private void ApplyDownloadedUpdate()
    {
        try
        {
            _updates.ApplyAfterExit(_restartAfterUpdate);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Velopack applies a downloaded package at the next start anyway.
        }
    }

    /// <summary>
    /// After an update that changed SbkMicFx.dll the microphones still run the old copy: offer to
    /// replace it (administrator prompt, audio restarts for a moment).
    /// </summary>
    public async Task OfferMicModuleRefreshAsync()
    {
        if (!MicModuleService.NeedsRefresh())
            return;
        if (!_dialogs.Confirm(
                "Questa versione contiene un modulo microfono aggiornato.\n\n" +
                "Installarlo ora? Serve il permesso di amministratore e l'audio di Windows si interrompe per un attimo. " +
                "Se rispondi No, il microfono continua con il modulo precedente e te lo richiederò al prossimo avvio."))
            return;
        var result = await MicModuleService.RefreshAsync();
        StatusText = result.Success ? "Modulo microfono aggiornato." : "Aggiornamento del modulo microfono non riuscito: " + result.Message;
        UpdateMicStatus();
    }
}
