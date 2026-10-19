using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.Core.Tests;

public class SoundFolderTests
{
    private static bool IsAudio(string path) => Path.GetExtension(path) is ".mp3" or ".wav";

    [Fact]
    public void Resolves_a_path_relative_to_the_app_or_a_fixed_one()
    {
        var app = Path.Combine(Path.GetTempPath(), "Sbk", "App");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "Sbk", "Suoni"), SoundFolder.Resolve(@"..\Suoni\", app));
        Assert.Equal(@"\\NAS\Public\Suoni", SoundFolder.Resolve(@"\\NAS\Public\Suoni", app));
        Assert.Null(SoundFolder.Resolve("  ", app));
        Assert.Null(SoundFolder.Resolve(null, app));
    }

    [Fact]
    public void Tells_the_path_relative_to_the_app_only_on_the_same_drive()
    {
        var app = Path.Combine(Path.GetTempPath(), "Sbk", "App");

        Assert.Equal(@"..\Suoni", SoundFolder.RelativeToApp(Path.Combine(Path.GetTempPath(), "Sbk", "Suoni"), app));
        Assert.Null(SoundFolder.RelativeToApp(@"\\NAS\Public\Suoni", app));
    }

    [Fact]
    public void Copies_in_keeping_files_already_there_and_reusing_identical_ones()
    {
        using var dir = new TempDirectory();
        var folder = Path.Combine(dir.Path, "Suoni");
        var inside = dir.CreateFile(@"Suoni\gia qui.mp3", "abc");
        var outside = dir.CreateFile(@"Download\airhorn.mp3", "1234");
        var sameName = dir.CreateFile(@"Altro\airhorn.mp3", "123456");

        Assert.Equal(inside, SoundFolder.CopyIn(inside, folder, "gia qui.mp3"));

        var copied = SoundFolder.CopyIn(outside, folder, @"Meme\airhorn.mp3");
        Assert.Equal(Path.Combine(folder, "Meme", "airhorn.mp3"), copied);
        Assert.Equal("1234", File.ReadAllText(copied));
        Assert.True(File.Exists(outside)); // copied, not moved

        // Same name and size: the same sound, reused. Same name, other content: a new name.
        Assert.Equal(copied, SoundFolder.CopyIn(outside, folder, @"Meme\airhorn.mp3"));
        Assert.Equal(Path.Combine(folder, "Meme", "airhorn (2).mp3"), SoundFolder.CopyIn(sameName, folder, @"Meme\airhorn.mp3"));
    }

    [Fact]
    public void Finds_new_files_relinks_missing_ones_and_skips_excluded_ones()
    {
        using var dir = new TempDirectory();
        var folder = Path.Combine(dir.Path, "Suoni");
        var known = dir.CreateFile(@"Suoni\conosciuto.mp3");
        var found = dir.CreateFile(@"Suoni\Sotto\spostato.mp3");
        var fresh = dir.CreateFile(@"Suoni\Sotto\nuovo.wav");
        dir.CreateFile(@"Suoni\tolto.mp3");
        dir.CreateFile(@"Suoni\note.txt");
        var moved = new Sound { Path = Path.Combine(dir.Path, "Vecchia", "spostato.mp3") };
        Sound[] sounds = [new Sound { Path = known }, moved];

        var scan = SoundFolder.Scan(folder, sounds, ["tolto.mp3"], IsAudio);

        Assert.Equal([fresh], scan.NewFiles);
        var (sound, path) = Assert.Single(scan.Relinked);
        Assert.Same(moved, sound);
        Assert.Equal(found, path);
    }

    [Fact]
    public void Keeps_uncategorized_sounds_in_their_own_category_first_in_the_tree()
    {
        Sound a = new() { Title = "a" }, b = new() { Title = "b" }, c = new() { Title = "c" };
        var voice = new Category { Name = "Voice", Children = [new Category { Name = "Teorema", SoundIds = [b.Id] }] };
        var library = new SoundLibrary { Sounds = [a, b, c], Categories = [voice] };

        Assert.True(library.SyncUncategorized());
        var uncategorized = library.Categories[0];
        Assert.True(uncategorized.IsUncategorized);
        Assert.Equal(SoundLibrary.UncategorizedName, uncategorized.Name);
        Assert.Equal([a.Id, c.Id], uncategorized.SoundIds);

        // Put in a category, a sound leaves it; the others keep their order and new ones go last.
        uncategorized.SoundIds.Reverse();
        voice.SoundIds.Add(a.Id);
        var d = new Sound { Title = "d" };
        library.Sounds.Add(d);
        library.RemoveSounds(new HashSet<Guid> { b.Id });

        Assert.False(library.SyncUncategorized());
        Assert.Equal([c.Id, d.Id], uncategorized.SoundIds);
        Assert.Equal(2, library.Categories.Count);
    }
}
