using System.IO;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.ViewModels;

/// <summary>What the settings dialog can do with the profile file.</summary>
public interface IProfileActions
{
    string ProfilePath { get; }
    string ExecutableProfilePath { get; }

    /// <summary>How the profile is found, in words.</summary>
    string ProfileLocationText { get; }

    /// <summary>Returns null on success, otherwise the reason it failed.</summary>
    string? MoveProfile(string path);

    string? ExportProfile(string path);

    /// <summary>Makes <paramref name="path"/> the profile used after the app restarts.</summary>
    string? UseProfile(string path);
}

// The profile: the library and the settings in one file the user can keep anywhere.
public sealed partial class MainViewModel : IProfileActions
{
    /// <summary>A different profile was chosen; the window restarts the app to open it.</summary>
    public event EventHandler? RestartRequested;

    public string ProfilePath => _storage.ProfilePath;
    public string ExecutableProfilePath => _storage.ExecutableProfilePath;

    public string ProfileLocationText => _storage.Location switch
    {
        ProfileLocation.NextToApp => "Accanto all'app: viene trovato anche spostando la cartella.",
        ProfileLocation.LinkedFromApp =>
            $"Relativo all'app ({_storage.RelativeToApp}, scritto in {ProfileStorage.LinkFileName}): " +
            "sposta insieme le cartelle e viene ritrovato.",
        ProfileLocation.ThisPc => "Percorso ricordato solo su questo PC.",
        _ => "Cartella dati di Windows (predefinita).",
    };

    private Profile CurrentProfile() => new() { Library = _library, Settings = _settings };

    private void LoadProfile()
    {
        try
        {
            _storage.LoadLocal();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Impostazioni di questo PC non leggibili, uso quelle predefinite: {ex.Message}");
        }

        try
        {
            var result = _storage.Open();
            (_library, _settings) = (result.Profile.Library, result.Profile.Settings);
            switch (result.State)
            {
                case ProfileOpenState.Migrated:
                    StatusText = $"Libreria e impostazioni ora sono nel profilo {_storage.ProfilePath}.";
                    break;
                case ProfileOpenState.Missing when !_dialogs.Confirm(
                    $"Il profilo {_storage.ProfilePath} non è stato trovato (forse è su un'unità scollegata).\n\n" +
                    "Sì: crea lì un profilo nuovo.\n" +
                    "No: parti con un profilo vuoto senza salvare nulla. Dalle impostazioni puoi aprirne o crearne un altro."):
                    _canSaveProfile = false;
                    StatusText = "Profilo non trovato: le modifiche di questa sessione non verranno salvate.";
                    break;
            }
        }
        catch (InvalidDataException ex)
        {
            // The corrupt file was moved aside, so starting fresh cannot overwrite it.
            _dialogs.ShowError(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _canSaveProfile = false;
            _dialogs.ShowError($"Impossibile leggere {_storage.ProfilePath}: {ex.Message}\nLe modifiche di questa sessione non verranno salvate.");
        }
    }

    public string? MoveProfile(string path)
    {
        _saveTimer.Stop();
        var error = Try(() => _storage.MoveTo(path, CurrentProfile()));
        if (error is null)
        {
            _canSaveProfile = true;
            OnPropertyChanged(nameof(ProfilePath));
        }
        return error;
    }

    public string? ExportProfile(string path) => Try(() => _storage.ExportCopy(path, CurrentProfile()));

    public string? UseProfile(string path)
    {
        // Keep what changed in the current profile before leaving it.
        SaveNow();
        return Try(() => _storage.Use(path));
    }

    private static string? Try(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Non ho il permesso di scrivere lì. Cartelle come Programmi richiedono i permessi di amministratore: scegline un'altra.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }
}
