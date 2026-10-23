using System.IO;
using CommunityToolkit.Mvvm.Input;
using SoundBoardKing.App.Services;
using SoundBoardKing.Audio.Editing;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.App.ViewModels;

// The sound editor: what it saves goes back into the library, as a new sound or in place of the old file.
public sealed partial class MainViewModel
{
    /// <summary>The view opens the editor on this sound.</summary>
    public event EventHandler<SoundItemViewModel>? EditorRequested;

    [RelayCommand]
    private void EditSound(SoundItemViewModel? sound)
    {
        sound ??= SelectedSound;
        if (sound is null)
            return;
        sound.RefreshFileState();
        if (sound.IsMissing)
        {
            StatusText = $"File non trovato: {sound.Model.Path}";
            return;
        }
        EditorRequested?.Invoke(this, sound);
    }

    /// <summary>
    /// Writes the edited sound over the original's file. In another format the file gets the new
    /// extension and the old one goes to the Recycle Bin. Returns why it failed, or null.
    /// </summary>
    public async Task<string?> ReplaceSoundFileAsync(SoundItemViewModel sound, AudioClip clip, ExportFormat format, int bitrate)
    {
        var original = sound.Model.Path;
        var target = Path.ChangeExtension(original, AudioExport.InfoOf(format).Extension);
        bool newFile = !string.Equals(target, original, StringComparison.OrdinalIgnoreCase);
        if (newFile && File.Exists(target))
            target = UniquePath(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target), Path.GetExtension(target));

        // The file is open while the sound plays: stop it and give the audio thread a moment to let go.
        await ReleaseFileAsync(sound);
        try
        {
            await Task.Run(() => SaveWithRetry(clip, target, format, bitrate));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return $"Impossibile salvare il suono: {ex.Message}";
        }

        string? note = null;
        if (newFile)
        {
            try
            {
                RecycleBin.Send(original);
            }
            catch (IOException)
            {
                note = $" Il vecchio file è rimasto al suo posto: {original}";
            }
            // The old file is not found again as a new sound, nor kept out of the folder's searches.
            ExcludeFromSoundFolder([original]);
            IncludeInSoundFolder([target]);
            sound.Model.Path = target;
        }
        UpdateAfterEdit(sound.Model, clip);
        sound.FileChanged();
        LibraryEdited();
        MeasureLoudness();
        StatusText = $"\"{sound.Title}\" modificato e salvato." + note;
        return null;
    }

    /// <summary>
    /// Saves the edited sound as a new file next to the original (in the sounds folder for a Myinstants
    /// download) and adds it to the library, in the same categories, right after the original.
    /// Returns why it failed, or null.
    /// </summary>
    public async Task<string?> AddEditedSoundAsync(SoundItemViewModel original, AudioClip clip, ExportFormat format, int bitrate, string title)
    {
        title = title.Trim();
        if (title.Length == 0)
            title = original.Title + " (modificato)";
        if (title.Length > MaxTitleLength)
            title = title[..MaxTitleLength];

        var folder = IsMyinstantsDownload(original.Model.Path) && SoundFolderPath is { } soundFolder
            ? soundFolder
            : Path.GetDirectoryName(original.Model.Path)!;
        var path = UniquePath(folder, SafeFileName(title), AudioExport.InfoOf(format).Extension);
        try
        {
            await Task.Run(() => SaveWithRetry(clip, path, format, bitrate));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return $"Impossibile salvare il suono: {ex.Message}";
        }

        var sound = new Sound
        {
            Title = title,
            Artist = original.Model.Artist,
            Path = path,
            Volume = original.Model.Volume,
        };
        UpdateAfterEdit(sound, clip);
        _library.Sounds.Insert(_library.Sounds.IndexOf(original.Model) + 1, sound);
        foreach (var category in _library.EnumerateCategories().Where(c => !c.IsUncategorized))
        {
            int index = category.SoundIds.IndexOf(original.Id);
            if (index >= 0)
                category.SoundIds.Insert(index + 1, sound.Id);
        }
        _soundsById[sound.Id] = new SoundItemViewModel(sound);
        IncludeInSoundFolder([path]);
        LibraryEdited();
        MeasureLoudness();
        SelectedSound = _soundsById[sound.Id];
        StatusText = $"Aggiunto \"{title}\".";
        return null;
    }

    private static void UpdateAfterEdit(Sound sound, AudioClip clip)
    {
        sound.DurationMs = (long)clip.Duration.TotalMilliseconds;
        // Measured again in the background: the edit may have changed how loud it is.
        sound.Loudness = null;
        sound.Peak = null;
    }

    private async Task ReleaseFileAsync(SoundItemViewModel sound)
    {
        _engine.StopTag(sound.Id);
        for (int i = 0; i < 50 && _engine.IsPlayingTag(sound.Id); i++)
            await Task.Delay(20);
    }

    // A file just closed by the audio thread, or scanned by an antivirus, can stay locked for a moment.
    private static void SaveWithRetry(AudioClip clip, string path, ExportFormat format, int bitrate)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                AudioExport.Save(clip, path, format, bitrate);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static string UniquePath(string folder, string name, string extension)
    {
        var path = Path.Combine(folder, name + extension);
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} ({n}){extension}");
        return path;
    }

    private static string SafeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(title.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "Suono" : name.Length > 120 ? name[..120] : name;
    }
}
