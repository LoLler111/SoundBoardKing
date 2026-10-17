using SoundBoardKing.Core.Import;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Core.Tests;

public class SplImporterTests
{
    private const string SampleList = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Soundlist>
          <Sound hash="1" url="Suoni\airhorn.mp3" artist="" title="airhorn" duration="0:03" addedOn="2021-04-24"/>
          <Sound hash="2" url="Suoni\conga.mp3" artist="Gloria" title="" duration="4:16" addedOn="2021-04-24"/>
          <Sound hash="3" url="C:\NonEsiste\gallo.mp3" artist="" title="Canto del gallo" duration="1:02:03"/>
          <Sound hash="4" url="Suoni\mancante.mp3" artist="" title="mancante" duration="0:01"/>
          <Categories>
            <Category type="1" icon="stock_icon_cat_list" hidden="true"/>
            <Category name="Voice" icon="gen_V">
              <Category name="Teorema" icon="gen_T">
                <Sound id="3" />
              </Category>
              <Sound id="1" />
              <Sound id="99" />
            </Category>
            <Category name="Musica" icon="gen_M">
              <Sound id="2" />
              <Sound id="1" />
            </Category>
          </Categories>
          <Hotbar>
            <HotbarPage>
              <HotbarItem id="0" soundId="2"/>
              <HotbarItem id="19" soundId="1"/>
              <HotbarItem id="4005" soundId="3"/>
              <HotbarItem id="7000" soundId="1"/>
            </HotbarPage>
          </Hotbar>
        </Soundlist>
        """;

    [Fact]
    public void Imports_sounds_categories_and_hotbar()
    {
        using var dir = new TempDirectory();
        var airhorn = dir.CreateFile(@"Suoni\airhorn.mp3");
        var conga = dir.CreateFile(@"Suoni\conga.mp3");
        var gallo = dir.CreateFile(@"Altro\gallo.mp3");
        var spl = dir.CreateFile(@"Configurazione\lista.spl", SampleList);

        var result = SplImporter.Import(spl);
        var sounds = result.Library.Sounds;

        Assert.Equal(4, sounds.Count);
        Assert.Equal(1, result.MissingFiles);

        // Relative path resolved from the list's parent folder.
        Assert.Equal(airhorn, sounds[0].Path);
        Assert.Equal(3_000, sounds[0].DurationMs);
        Assert.Equal(new DateOnly(2021, 4, 24), sounds[0].AddedOn);

        // Empty title falls back to the file name.
        Assert.Equal("conga", sounds[1].Title);
        Assert.Equal("Gloria", sounds[1].Artist);
        Assert.Equal(conga, sounds[1].Path);

        // Absolute path from another PC relinked by file name.
        Assert.Equal(gallo, sounds[2].Path);
        Assert.Equal((1 * 3600 + 2 * 60 + 3) * 1000L, sounds[2].DurationMs);

        // Built-in category skipped, nesting kept, references mapped by position.
        var categories = result.Library.Categories;
        Assert.Equal(["Voice", "Musica"], categories.Select(c => c.Name));
        Assert.Equal([sounds[0].Id], categories[0].SoundIds);
        Assert.Equal("Teorema", Assert.Single(categories[0].Children).Name);
        Assert.Equal([sounds[2].Id], categories[0].Children[0].SoundIds);
        Assert.Equal([sounds[1].Id, sounds[0].Id], categories[1].SoundIds);

        var hotbar = result.Library.Hotbar;
        Assert.Equal(sounds[1].Id, hotbar.Get(0, 0));
        Assert.Equal(sounds[0].Id, hotbar.Get(0, 19));
        Assert.Equal(sounds[2].Id, hotbar.Get(4, 5));
        Assert.Equal(3, hotbar.Slots.Count);

        // Dangling category reference and out-of-range slot are reported, not fatal.
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void Rejects_files_that_are_not_sound_lists()
    {
        using var dir = new TempDirectory();
        var file = dir.CreateFile("altro.spl", "<Playlist/>");

        Assert.Throws<InvalidDataException>(() => SplImporter.Import(file));
    }

    [Theory]
    [InlineData("0:30", 30_000)]
    [InlineData("3:14", 194_000)]
    [InlineData("1:00:00", 3_600_000)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    public void Parses_durations(string text, long expected) =>
        Assert.Equal(expected, SplImporter.ParseDuration(text));

    /// <summary>Smoke test against the real SoundPad list on the development PC; skipped elsewhere.</summary>
    [Fact]
    public void Imports_the_real_soundpad_list_when_present()
    {
        const string realList = @"C:\Sviluppo\SoundPad_Licensed\Configurazione\ListaSuoni.spl";
        if (!File.Exists(realList))
            return;

        var result = SplImporter.Import(realList);

        Assert.Equal(985, result.Library.Sounds.Count);
        Assert.Equal(Hotbar.PageCount * Hotbar.SlotsPerPage, result.Library.Hotbar.Slots.Count);
        Assert.Equal(5, result.Library.Categories.Count);
        // 10 sounds live on a NAS share and 1 in another user's Downloads folder; the D:\ paths relink by name.
        Assert.Equal(11, result.MissingFiles);
    }
}
