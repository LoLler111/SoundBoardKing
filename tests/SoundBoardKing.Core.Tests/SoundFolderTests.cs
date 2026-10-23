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
    public void A_folder_chosen_for_the_first_time_is_relative_only_next_to_the_app()
    {
        var sbk = Path.Combine(Path.GetTempPath(), "Sbk");
        var app = Path.Combine(sbk, "App");

        Assert.Equal(@"..\Suoni", SoundFolder.SettingFor(Path.Combine(sbk, "Suoni"), app));
        Assert.Equal(Path.Combine(app, "Suoni"), Path.Combine(app, SoundFolder.SettingFor(Path.Combine(app, "Suoni"), app)));
        Assert.Equal(@"D:\Altrove\Suoni", SoundFolder.SettingFor(@"D:\Altrove\Suoni", app));
        Assert.Equal(@"\\NAS\Suoni", SoundFolder.SettingFor(@"\\NAS\Suoni", app));
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
    public void A_new_folder_takes_over_the_old_one_following_its_subfolders()
    {
        using var dir = new TempDirectory();
        var old = Path.Combine(dir.Path, "Vecchia");
        var folder = Path.Combine(dir.Path, "Nuova");
        // Copied, not moved: the old files are still there.
        var meme = new Sound { Path = dir.CreateFile(@"Vecchia\Meme\airhorn.mp3", "meme") };
        var film = new Sound { Path = dir.CreateFile(@"Vecchia\Film\airhorn.mp3", "film!") };
        var gone = new Sound { Path = Path.Combine(old, "Voci", "ciao.mp3") };
        var notCopied = new Sound { Path = dir.CreateFile(@"Vecchia\solo qui.mp3") };
        var elsewhere = new Sound { Path = dir.CreateFile(@"Download\boom.mp3", "boom") };
        var changed = new Sound { Path = dir.CreateFile(@"Download\bip.mp3", "bip") };
        var newFilm = dir.CreateFile(@"Nuova\Film\airhorn.mp3", "film!");
        var newMeme = dir.CreateFile(@"Nuova\Meme\airhorn.mp3", "meme");
        var newCiao = dir.CreateFile(@"Nuova\Altro\Voci\ciao.mp3");
        var newBoom = dir.CreateFile(@"Nuova\boom.mp3", "boom");
        var otherBip = dir.CreateFile(@"Nuova\bip.mp3", "un altro bip");
        Sound[] sounds = [meme, film, gone, notCopied, elsewhere, changed];

        var scan = SoundFolder.Scan(folder, sounds, [], IsAudio, folderChanged: true, previousFolder: old);

        var relinked = scan.Relinked.ToDictionary(r => r.Sound, r => r.Path);
        Assert.Equal(newMeme, relinked[meme]);
        Assert.Equal(newFilm, relinked[film]);
        Assert.Equal(newCiao, relinked[gone]);
        Assert.Equal(newBoom, relinked[elsewhere]); // same name and size: the same sound
        Assert.Equal(4, relinked.Count);
        Assert.Equal([otherBip], scan.NewFiles); // same name, other content: a new sound

        // Without a change of folder, files still in place are left alone.
        var again = SoundFolder.Scan(folder, sounds, [], IsAudio);
        Assert.Equal([gone], again.Relinked.Select(r => r.Sound));
    }

    [Fact]
    public void A_missing_sound_prefers_the_file_under_the_same_subfolders()
    {
        using var dir = new TempDirectory();
        var folder = Path.Combine(dir.Path, "Suoni");
        var other = dir.CreateFile(@"Suoni\Altro\airhorn.mp3");
        var right = dir.CreateFile(@"Suoni\Meme\airhorn.mp3");
        var lost = new Sound { Path = Path.Combine(dir.Path, "Chiavetta", "Meme", "airhorn.mp3") };

        var scan = SoundFolder.Scan(folder, [lost], [], IsAudio);

        Assert.Equal(right, Assert.Single(scan.Relinked).Path);
        Assert.Equal([other], scan.NewFiles);
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
