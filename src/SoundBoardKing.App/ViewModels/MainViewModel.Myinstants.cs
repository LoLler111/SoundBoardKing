using System.IO;
using System.Net.Http;
using SoundBoardKing.App.Services;
using SoundBoardKing.Audio;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Myinstants;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly Lazy<MyinstantsClient> SharedMyinstants = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SoundBoardKing/{UpdateService.CurrentVersion}");
        return new MyinstantsClient(http);
    });

    // Downloads that could not be deleted yet (the sound was still playing): tried again on exit.
    private readonly List<string> _downloadsToDelete = [];

    public MyinstantsClient Myinstants => SharedMyinstants.Value;

    /// <summary>
    /// Where the sounds added from Myinstants are saved: in the sounds folder, or without one next
    /// to the profile, so they travel with it (sounds near the profile are stored with a relative path).
    /// </summary>
    public string MyinstantsFolder => Path.Combine(SoundFolderPath ?? ProfileMyinstantsParent, "Myinstants");

    private string ProfileMyinstantsParent => Path.GetDirectoryName(_storage.ProfilePath)!;

    // Downloads made before choosing a sounds folder stay next to the profile and are still recognized.
    private IEnumerable<string> MyinstantsFolders =>
        new[] { MyinstantsFolder, Path.Combine(ProfileMyinstantsParent, "Myinstants") }.Distinct(StringComparer.OrdinalIgnoreCase);

    public string? OutputDeviceId => _storage.Local.OutputDeviceId;

    /// <summary>Volume of the Myinstants preview, 0..1; saved on this PC only.</summary>
    public float PreviewVolume
    {
        get => _storage.Local.PreviewVolume;
        set
        {
            _storage.Local.PreviewVolume = Math.Clamp(value, 0f, 1f);
            _storage.SaveLocal();
        }
    }

    /// <summary>The category selected in the main window; null for "all sounds" and "Senza categoria".</summary>
    public Category? SelectedCategoryModel => TargetCategory;

    /// <summary>Every category a sound can be put in, in tree order, with how deep it sits (0 = top level).</summary>
    public IReadOnlyList<(Category Category, int Depth)> CategoriesInOrder()
    {
        var list = new List<(Category, int)>();
        void Walk(IEnumerable<Category> categories, int depth)
        {
            foreach (var category in categories.Where(c => !c.IsUncategorized))
            {
                list.Add((category, depth));
                Walk(category.Children, depth + 1);
            }
        }
        Walk(_library.Categories, 0);
        return list;
    }

    /// <summary>Asks for a name and adds a top-level category; null when cancelled.</summary>
    public Category? AskNewCategory() => CreateCategory(null);

    private bool IsMyinstantsDownload(string path) => MyinstantsFolders.Any(folder => SoundFolder.Contains(folder, path));

    private string MyinstantsPathOf(MyinstantsSound sound) => Path.Combine(MyinstantsFolder, MyinstantsClient.FileNameOf(sound));

    /// <summary>The library sound already made from this Myinstants sound, if any.</summary>
    public SoundItemViewModel? FindMyinstantsSound(MyinstantsSound sound)
    {
        var paths = MyinstantsFolders.Select(folder => Path.Combine(folder, MyinstantsClient.FileNameOf(sound))).ToList();
        return _soundsById.Values.FirstOrDefault(s => paths.Contains(s.Model.Path, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Saves an mp3 already downloaded (for the preview) and adds it to the library and to
    /// <paramref name="category"/> (none: library only). Written next to its final name and renamed
    /// at the end, so a failure never leaves half a file behind.
    /// </summary>
    public async Task<SoundItemViewModel?> AddMyinstantsSoundAsync(MyinstantsSound sound, byte[] data, LoudnessInfo? loudness, Category? category)
    {
        if (FindMyinstantsSound(sound) is { } existing)
        {
            StatusText = $"\"{existing.Title}\" è già nella libreria.";
            return existing;
        }

        var path = MyinstantsPathOf(sound);
        var partial = path + ".part";
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(MyinstantsFolder);
                File.WriteAllBytes(partial, data);
                File.Move(partial, path, overwrite: true);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            StatusText = $"Impossibile salvare \"{sound.Title}\": {ex.Message}";
            return null;
        }

        var model = new Sound
        {
            Title = sound.Title.Length > MaxTitleLength ? sound.Title[..MaxTitleLength] : sound.Title,
            Path = path,
            DurationMs = (long)(await Task.Run(() => AudioFileProbe.TryGetDuration(path)) ?? TimeSpan.Zero).TotalMilliseconds,
            // Measured already for the preview: no need to decode it again.
            Loudness = loudness?.Lufs,
            Peak = loudness?.PeakDb,
        };
        AddToLibrary([model], category);
        return _soundsById[model.Id];
    }

    /// <summary>Adds the sound a pasted or dropped Myinstants link points at.</summary>
    public async Task AddMyinstantsLinkAsync(string link)
    {
        StatusText = "Scaricamento da Myinstants…";
        try
        {
            var sound = await Myinstants.ResolveAsync(link);
            if (sound is null)
            {
                StatusText = "Nel link di Myinstants non c'è nessun suono.";
                return;
            }
            if (FindMyinstantsSound(sound) is { } existing)
            {
                StatusText = $"\"{existing.Title}\" è già nella libreria.";
                return;
            }
            var data = await Myinstants.DownloadAsync(sound);
            var loudness = await Task.Run(() => PreviewPlayer.TryMeasure(data));
            if (loudness is null)
            {
                StatusText = $"\"{sound.Title}\" non è un file audio valido.";
                return;
            }
            // Like files dropped on the window: into the category selected there.
            await AddMyinstantsSoundAsync(sound, data, loudness, SelectedCategory?.Model);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            StatusText = $"Myinstants non risponde: {ex.Message}";
        }
    }

    /// <summary>Deletes the Myinstants downloads among <paramref name="paths"/> that no sound uses any more.</summary>
    private void DeleteUnusedDownloads(IEnumerable<string> paths)
    {
        var used = _library.Sounds.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(p => IsMyinstantsDownload(p) && !used.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryDelete(path))
                _downloadsToDelete.Add(path);
        }
    }

    private void DeletePendingDownloads()
    {
        foreach (var path in _downloadsToDelete)
            TryDelete(path);
        _downloadsToDelete.Clear();
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
