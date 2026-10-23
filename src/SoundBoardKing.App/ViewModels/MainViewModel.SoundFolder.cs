using System.IO;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.Audio;
using SoundBoardKing.Core.Import;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.ViewModels;

// The sounds folder: one folder with every sound file, next to the app or on a NAS. New files found
// there join the library ("Senza categoria" until put in a category); sounds added from elsewhere
// are copied into it.
public sealed partial class MainViewModel
{
    private bool _scanningSoundFolder;
    private bool _soundFolderOffered;

    /// <summary>The folder relative sounds-folder paths start from: the one the user sees the app in.</summary>
    public string AppFolder => _storage.ExecutableDirectory;

    /// <summary>The sounds folder as a full path; null when none is chosen.</summary>
    public string? SoundFolderPath => SoundFolder.Resolve(_settings.SoundsFolder, AppFolder);

    [RelayCommand]
    private Task SearchNewSoundsAsync() => ScanSoundFolderAsync(atStartup: false);

    /// <summary>
    /// Adds the sounds folder's new files to the library and relinks missing sounds found there.
    /// At startup it says nothing when there is no folder or nothing new. Right after choosing
    /// another folder (<paramref name="folderChanged"/>), every sound is relinked to its file in it,
    /// following the subfolders of <paramref name="previousFolder"/>.
    /// </summary>
    public async Task ScanSoundFolderAsync(bool atStartup, bool folderChanged = false, string? previousFolder = null)
    {
        if (_scanningSoundFolder)
            return;
        if (SoundFolderPath is not { } folder)
        {
            if (!atStartup)
                StatusText = "Scegli prima la cartella dei suoni nelle impostazioni.";
            return;
        }

        _scanningSoundFolder = true;
        try
        {
            StatusText = "Cerco nuovi suoni nella cartella dei suoni…";
            // Read on another thread (a NAS can be slow), from copies of what the scan looks at.
            var sounds = _library.Sounds.ToList();
            var excluded = _library.ExcludedFiles.ToList();
            SoundFolderScan scan;
            List<Sound> found;
            try
            {
                scan = await Task.Run(() =>
                    SoundFolder.Scan(folder, sounds, excluded, AudioFileProbe.IsSupported, folderChanged, previousFolder));
                found = await Task.Run(() => scan.NewFiles.Select(NewSoundFrom).ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText = Directory.Exists(folder)
                    ? $"Impossibile leggere la cartella dei suoni: {ex.Message}"
                    : $"Cartella dei suoni non trovata: {folder}";
                return;
            }

            int relinked = 0;
            foreach (var (sound, path) in scan.Relinked.Where(r => _soundsById.ContainsKey(r.Sound.Id)))
            {
                sound.Path = path;
                _soundsById[sound.Id].RefreshFileState();
                relinked++;
            }
            // Anything added while the scan ran (a drop, Myinstants) is not added twice.
            var known = _library.Sounds.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            found.RemoveAll(s => known.Contains(s.Path));
            if (found.Count > 0)
                AddToLibrary(found, null);
            else if (relinked > 0)
            {
                RefreshCounts();
                LibraryEdited();
            }

            if (folderChanged)
            {
                // Sounds of the old folder not found in the new one keep playing from where they are.
                int left = previousFolder is null ? 0
                    : _library.Sounds.Count(s => s.Path.Length > 0 && SoundFolder.Contains(previousFolder, s.Path));
                var parts = new List<string>();
                if (relinked > 0)
                    parts.Add($"ricollegati {relinked} suoni");
                if (found.Count > 0)
                    parts.Add($"{found.Count} nuovi in \"{SoundLibrary.UncategorizedName}\"");
                if (left > 0)
                    parts.Add($"{left} non trovati, restano nella vecchia cartella");
                StatusText = "Nuova cartella dei suoni: " + (parts.Count > 0 ? string.Join(", ", parts) : "nessun suono") + ".";
                return;
            }
            StatusText = (found.Count, relinked) switch
            {
                (0, 0) => atStartup ? "" : "Nessun nuovo suono nella cartella dei suoni.",
                (_, 0) => $"Dalla cartella dei suoni: {found.Count} nuovi suoni, in \"{SoundLibrary.UncategorizedName}\".",
                (0, _) => $"Dalla cartella dei suoni: ritrovati {relinked} suoni che mancavano.",
                _ => $"Dalla cartella dei suoni: {found.Count} nuovi suoni, in \"{SoundLibrary.UncategorizedName}\", e ritrovati {relinked} che mancavano.",
            };
        }
        finally
        {
            _scanningSoundFolder = false;
        }
    }

    /// <summary>
    /// Without a sounds folder, the first sound added from elsewhere or from Myinstants asks for one
    /// (once per session), suggesting the folder most sounds are in. True when one was just chosen:
    /// the caller then looks through it once its sounds are added.
    /// </summary>
    private bool OfferSoundFolder()
    {
        if (SoundFolderPath is not null || _soundFolderOffered)
            return false;
        _soundFolderOffered = true;

        const string why = "Non hai ancora scelto la cartella dei suoni: i suoni di Myinstants e quelli aggiunti " +
            "da fuori vengono salvati lì, così stanno tutti insieme.";
        string? chosen = null;
        if (MostUsedSoundFolder() is { } suggested)
        {
            chosen = _dialogs.Confirm($"{why}\n\nUsare la cartella dove sono quasi tutti i tuoi suoni?\n{suggested}",
                "Usa questa", "Scegli un'altra…")
                ? suggested
                : _dialogs.PickFolder("Cartella dei suoni", suggested);
        }
        else if (_dialogs.Confirm($"{why}\n\nSceglierla ora?", "Scegli…", "Non ora"))
        {
            chosen = _dialogs.PickFolder("Cartella dei suoni", null);
        }
        if (chosen is null)
            return false;

        _settings.SoundsFolder = SoundFolder.SettingFor(chosen, AppFolder);
        ScheduleSave();
        return true;
    }

    private string? MostUsedSoundFolder() => _library.Sounds
        .Where(s => s.Path.Length > 0 && !IsMyinstantsDownload(s.Path))
        .GroupBy(s => Path.GetDirectoryName(s.Path), StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(g => g.Count())
        .Select(g => g.Key)
        .FirstOrDefault(Directory.Exists);

    private static Sound NewSoundFrom(string file) => new()
    {
        Title = TitleCleaner.Clean(Path.GetFileNameWithoutExtension(file)),
        Path = file,
        DurationMs = (long)(AudioFileProbe.TryGetDuration(file)?.TotalMilliseconds ?? 0),
    };

    /// <summary>
    /// Copies files from elsewhere into the sounds folder (keeping a dropped folder's subfolders) and
    /// returns the paths to add. Without a sounds folder, or when copying fails, files stay where they are.
    /// </summary>
    private static (List<string> Paths, string? Error) CopyIntoSoundFolder(
        IEnumerable<(string File, string RelativeTarget)> files, string? folder)
    {
        var paths = new List<string>();
        string? error = null;
        foreach (var (file, relativeTarget) in files)
        {
            var path = file;
            if (folder is not null && error is null)
            {
                try
                {
                    path = SoundFolder.CopyIn(file, folder, relativeTarget);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = ex.Message; // e.g. the NAS is off: the rest is not even tried
                }
            }
            paths.Add(path);
        }
        return (paths, error);
    }

    /// <summary>The sounds folder's files removed from the library are not brought back by the next search.</summary>
    private void ExcludeFromSoundFolder(IEnumerable<string> paths)
    {
        if (SoundFolderPath is not { } folder)
            return;
        foreach (var path in paths.Where(p => SoundFolder.Contains(folder, p) && !IsMyinstantsDownload(p)))
        {
            var relative = Path.GetRelativePath(folder, path);
            if (!_library.ExcludedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                _library.ExcludedFiles.Add(relative);
        }
    }

    /// <summary>Added again by hand: the next search may find them again too.</summary>
    private void IncludeInSoundFolder(IEnumerable<string> paths)
    {
        if (SoundFolderPath is not { } folder)
            return;
        var relative = paths.Where(p => SoundFolder.Contains(folder, p))
            .Select(p => Path.GetRelativePath(folder, p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _library.ExcludedFiles.RemoveAll(relative.Contains);
    }

    /// <summary>Where a sound added to the selected category goes: "Senza categoria" fills itself.</summary>
    private Category? TargetCategory => SelectedCategory?.Model is { IsUncategorized: false } category ? category : null;
}
