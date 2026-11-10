using System.IO;
using System.Runtime.InteropServices;
using SoundBoardKing.Audio.Editing;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Video;

namespace SoundBoardKing.App.ViewModels;

// Sounds taken from a video (a file, or a link to YouTube, TikTok, Instagram, Facebook…).
public sealed partial class MainViewModel
{
    /// <summary>FFmpeg, yt-dlp and Deno, downloaded the first time a video is opened.</summary>
    public VideoTools VideoTools { get; } = new(VideoTools.DefaultFolder);

    /// <summary>Where the sounds taken from videos are saved: like the Myinstants ones, in the sounds folder or next to the profile.</summary>
    public string VideoSoundsFolder => Path.Combine(SoundFolderPath ?? ProfileMyinstantsParent, "Da video");

    /// <summary>
    /// Saves a piece of a video's audio as a new sound and adds it to the library and to
    /// <paramref name="category"/> (none: library only). Returns the new sound, or null with
    /// <paramref name="error"/> saying why.
    /// </summary>
    public async Task<(SoundItemViewModel? Sound, string? Error)> AddVideoSoundAsync(AudioClip clip, string title, Category? category,
        ExportFormat format, int bitrate)
    {
        title = title.Trim();
        if (title.Length == 0)
            title = "Suono da video";
        if (title.Length > MaxTitleLength)
            title = title[..MaxTitleLength];

        bool folderChosen = OfferSoundFolder();
        var folder = VideoSoundsFolder;
        string path;
        try
        {
            Directory.CreateDirectory(folder);
            path = UniquePath(folder, SafeFileName(title), AudioExport.InfoOf(format).Extension);
            await Task.Run(() => SaveWithRetry(clip, path, format, bitrate));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            return (null, $"Impossibile salvare il suono: {ex.Message}");
        }

        var sound = new Sound { Title = title, Path = path };
        UpdateAfterEdit(sound, clip);
        IncludeInSoundFolder([path]);
        AddToLibrary([sound], category);
        StatusText = category is null ? $"Aggiunto \"{title}\"." : $"Aggiunto \"{title}\" alla categoria \"{category.Name}\".";
        if (folderChosen)
            await ScanSoundFolderAsync(atStartup: false, folderChanged: true);
        return (_soundsById[sound.Id], null);
    }
}
