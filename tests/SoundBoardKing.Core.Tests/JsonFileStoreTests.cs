using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.Core.Tests;

public class JsonFileStoreTests
{
    [Fact]
    public void Missing_file_loads_defaults()
    {
        using var dir = new TempDirectory();
        var store = new JsonFileStore<AppSettings>(Path.Combine(dir.Path, "settings.json"));

        Assert.Equal(0.8f, store.Load().SpeakerVolume);
    }

    [Fact]
    public void Library_round_trips()
    {
        using var dir = new TempDirectory();
        var store = new JsonFileStore<SoundLibrary>(Path.Combine(dir.Path, "sub", "library.json"));
        var sound = new Sound { Title = "airhorn", Path = @"D:\a.mp3", DurationMs = 3000, Hotkey = "Ctrl+1", AddedOn = new DateOnly(2021, 4, 24) };
        var library = new SoundLibrary { Sounds = [sound] };
        library.Categories.Add(new Category { Name = "Voice", SoundIds = [sound.Id], Children = [new Category { Name = "Teorema" }] });
        library.Hotbar.Set(4, 19, sound.Id);

        store.Save(library);
        var loaded = store.Load();

        var loadedSound = Assert.Single(loaded.Sounds);
        Assert.Equal(sound.Id, loadedSound.Id);
        Assert.Equal("Ctrl+1", loadedSound.Hotkey);
        Assert.Equal(new DateOnly(2021, 4, 24), loadedSound.AddedOn);
        Assert.Equal("Teorema", loaded.Categories[0].Children[0].Name);
        Assert.Equal(sound.Id, loaded.Hotbar.Get(4, 19));
    }

    [Fact]
    public void Corrupt_file_is_moved_aside()
    {
        using var dir = new TempDirectory();
        var path = dir.CreateFile("library.json", "{ non è json");
        var store = new JsonFileStore<SoundLibrary>(path);

        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(dir.Path, "library.json.corrupt-*"));
    }

    [Fact]
    public void Removing_sounds_clears_every_reference()
    {
        var keep = new Sound { Title = "keep" };
        var drop = new Sound { Title = "drop" };
        var child = new Category { Name = "child", SoundIds = [drop.Id, keep.Id] };
        var library = new SoundLibrary { Sounds = [keep, drop] };
        library.Categories.Add(new Category { Name = "root", SoundIds = [drop.Id], Children = [child] });
        library.Hotbar.Set(0, 0, drop.Id);
        library.Hotbar.Set(0, 1, keep.Id);

        library.RemoveSounds(new HashSet<Guid> { drop.Id });

        Assert.Equal([keep], library.Sounds);
        Assert.Empty(library.Categories[0].SoundIds);
        Assert.Equal([keep.Id], child.SoundIds);
        Assert.Null(library.Hotbar.Get(0, 0));
        Assert.Equal(keep.Id, library.Hotbar.Get(0, 1));
    }
}
