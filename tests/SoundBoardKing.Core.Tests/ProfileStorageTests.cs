using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.Core.Tests;

public class ProfileStorageTests
{
    private static ProfileStorage Open(TempDirectory dir)
    {
        var storage = new ProfileStorage(new AppPaths(Path.Combine(dir.Path, "appdata")), Path.Combine(dir.Path, "exe"));
        storage.LoadLocal();
        return storage;
    }

    private static Profile SampleProfile(string soundPath)
    {
        var sound = new Sound { Title = "airhorn", Path = soundPath, Hotkey = "Ctrl+1" };
        var profile = new Profile { Library = new SoundLibrary { Sounds = [sound] } };
        profile.Library.Categories.Add(new Category { Name = "Voice", SoundIds = [sound.Id] });
        profile.Library.Hotbar.Set(1, 2, sound.Id);
        profile.Settings.MicVolume = 0.5f;
        profile.Settings.StopHotkey = "Ctrl+Alt+S";
        return profile;
    }

    [Fact]
    public void Starts_empty_in_the_app_data_folder()
    {
        using var dir = new TempDirectory();
        var storage = Open(dir);

        var result = storage.Open();

        Assert.Equal(ProfileOpenState.Created, result.State);
        Assert.Equal(storage.DefaultProfilePath, storage.ProfilePath);
    }

    [Fact]
    public void Migrates_the_old_library_and_settings_keeping_devices_local()
    {
        using var dir = new TempDirectory();
        var paths = new AppPaths(Path.Combine(dir.Path, "appdata"));
        var sound = new Sound { Title = "old", Path = @"D:\old.mp3" };
        new JsonFileStore<SoundLibrary>(paths.LegacyLibraryFile).Save(new SoundLibrary { Sounds = [sound] });
        File.WriteAllText(paths.LegacySettingsFile,
            """{ "outputDeviceId": "{0.0.0.1}", "lastImportFolder": "D:\\spl", "micVolume": 0.25, "pauseHotkey": "F9" }""");

        var storage = Open(dir);
        var result = storage.Open();

        Assert.Equal(ProfileOpenState.Migrated, result.State);
        Assert.Equal(sound.Id, Assert.Single(result.Profile.Library.Sounds).Id);
        Assert.Equal(0.25f, result.Profile.Settings.MicVolume);
        Assert.Equal("F9", result.Profile.Settings.PauseHotkey);
        Assert.Equal("{0.0.0.1}", storage.Local.OutputDeviceId);
        Assert.Equal(@"D:\spl", storage.Local.LastImportFolder);
        Assert.True(File.Exists(paths.DefaultProfileFile));
        Assert.False(File.Exists(paths.LegacyLibraryFile));
        Assert.True(File.Exists(paths.LegacySettingsFile + ".migrated"));

        // The device id is not exported, and the next start reads the profile directly.
        Assert.DoesNotContain("0.0.0.1", File.ReadAllText(paths.DefaultProfileFile));
        var again = Open(dir);
        Assert.Equal(ProfileOpenState.Loaded, again.Open().State);
        Assert.Equal("{0.0.0.1}", again.Local.OutputDeviceId);
    }

    [Fact]
    public void Moving_next_to_the_executable_is_found_automatically()
    {
        using var dir = new TempDirectory();
        var storage = Open(dir);
        storage.Save(SampleProfile(@"D:\a.mp3"));

        storage.MoveTo(storage.ExecutableProfilePath, SampleProfile(@"D:\a.mp3"));

        Assert.False(File.Exists(storage.DefaultProfilePath));
        Assert.True(File.Exists(storage.ExecutableProfilePath));
        Assert.Null(storage.Local.ProfilePath);

        // A PC that never saw this profile still finds it next to the executable.
        File.Delete(Path.Combine(dir.Path, "appdata", "local.json"));
        var fresh = Open(dir);
        var result = fresh.Open();
        Assert.Equal(fresh.ExecutableProfilePath, fresh.ProfilePath);
        Assert.Equal(ProfileOpenState.Loaded, result.State);
        Assert.Equal("Ctrl+Alt+S", result.Profile.Settings.StopHotkey);
        Assert.Equal("Voice", result.Profile.Library.Categories[0].Name);
        Assert.NotNull(result.Profile.Library.Hotbar.Get(1, 2));
    }

    [Fact]
    public void A_chosen_location_is_remembered()
    {
        using var dir = new TempDirectory();
        var storage = Open(dir);
        var chosen = Path.Combine(dir.Path, "sync", "mine.sbk");

        storage.MoveTo(chosen, SampleProfile(@"D:\a.mp3"));

        var again = Open(dir);
        Assert.Equal(chosen, again.ProfilePath);
        Assert.Equal(ProfileOpenState.Loaded, again.Open().State);
    }

    [Fact]
    public void Missing_chosen_profile_is_reported_not_replaced()
    {
        using var dir = new TempDirectory();
        var storage = Open(dir);
        var chosen = Path.Combine(dir.Path, "usb", "mine.sbk");
        storage.MoveTo(chosen, SampleProfile(@"D:\a.mp3"));
        File.Delete(chosen);

        var again = Open(dir);

        Assert.Equal(ProfileOpenState.Missing, again.Open().State);
        Assert.Equal(chosen, again.ProfilePath);
    }

    [Fact]
    public void Sounds_inside_the_profile_folder_follow_it_when_copied()
    {
        using var dir = new TempDirectory();
        var storage = Open(dir);
        var inside = dir.CreateFile(Path.Combine("exe", "Sounds", "horn.mp3"));
        storage.MoveTo(storage.ExecutableProfilePath, SampleProfile(inside));

        Assert.Contains("Sounds\\\\horn.mp3", File.ReadAllText(storage.ExecutableProfilePath));
        Assert.DoesNotContain(dir.Path.Replace("\\", "\\\\"), File.ReadAllText(storage.ExecutableProfilePath));

        var copy = Path.Combine(dir.Path, "elsewhere");
        Directory.CreateDirectory(Path.Combine(copy, "Sounds"));
        File.Copy(storage.ExecutableProfilePath, Path.Combine(copy, ProfileStorage.FileName));
        var moved = new ProfileStorage(new AppPaths(Path.Combine(dir.Path, "other-pc")), copy);
        moved.LoadLocal();

        var sound = Assert.Single(moved.Open().Profile.Library.Sounds);
        Assert.Equal(Path.Combine(copy, "Sounds", "horn.mp3"), sound.Path);
    }

    [Fact]
    public void Using_another_file_rejects_what_is_not_a_profile()
    {
        using var dir = new TempDirectory();
        var storage = Open(dir);
        var other = dir.CreateFile("other.json", """{ "something": 1 }""");
        var broken = dir.CreateFile("broken.sbk", "{ non è json");

        Assert.Throws<InvalidDataException>(() => storage.Use(other));
        Assert.Throws<InvalidDataException>(() => storage.Use(broken));
        Assert.True(File.Exists(broken)); // left where it was
        Assert.Equal(storage.DefaultProfilePath, storage.ProfilePath);

        var exported = Path.Combine(dir.Path, "export", "copy.sbk");
        storage.ExportCopy(exported, SampleProfile(@"D:\a.mp3"));
        storage.Use(exported);
        Assert.Equal(exported, Open(dir).ProfilePath);
    }
}
