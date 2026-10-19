using SoundBoardKing.Core.Myinstants;

namespace SoundBoardKing.Core.Tests;

public class MyinstantsParserTests
{
    // The shape of a result on the site's list pages (search, trending), trimmed.
    private const string ListPage = """
        <div id="instants_container">
          <div class="instants result-page">
            <div class="instant">
              <div class="circle small-button-background" style="background-color:#ffff33;"></div>
              <button class="small-button" onclick="play('/media/sounds/movie_1.mp3', 'loader-23010', 'bruh')" title="Riprodurre il suono di BRUH" type="button"></button>
              <div id="loader-23010" class="loader"></div>
              <a href="/it/instant/bruh/" class="instant-link link-secondary">BRUH</a>
              <div class="result-page-instant-sharebox">
                <button type="button" class="instant-action-button" onclick="favorite('23010')"></button>
              </div>
            </div>
            <div class="instant">
              <button class="small-button" onclick="play('/media/sounds/l\u0027anatra_x1.mp3', 'loader-2', 'anatra-2')" type="button"></button>
              <a href="/it/instant/anatra-2/" class="instant-link link-secondary">L&#x27;anatra &amp;   co.</a>
            </div>
            <div class="instant">
              <button class="small-button" onclick="play('/media/sounds/movie_1.mp3', 'loader-3', 'bruh-copy')" type="button"></button>
              <a href="/it/instant/bruh-copy/" class="instant-link link-secondary">BRUH again</a>
            </div>
            <div class="instant">
              <button class="small-button" onclick="play('https://evil.example/media/sounds/x.mp3', 'loader-4', 'x')" type="button"></button>
              <a href="/it/instant/x/" class="instant-link link-secondary">Elsewhere</a>
            </div>
          </div>
        </div>
        """;

    [Fact]
    public void Reads_title_mp3_and_page_of_each_result()
    {
        var sounds = MyinstantsParser.ParseList(ListPage);

        Assert.Equal(2, sounds.Count);
        Assert.Equal("BRUH", sounds[0].Title);
        Assert.Equal("https://www.myinstants.com/media/sounds/movie_1.mp3", sounds[0].SoundUrl.AbsoluteUri);
        Assert.Equal("https://www.myinstants.com/it/instant/bruh/", sounds[0].PageUrl?.AbsoluteUri);
    }

    [Fact]
    public void Decodes_html_and_javascript_escapes()
    {
        var sound = MyinstantsParser.ParseList(ListPage)[1];

        Assert.Equal("L'anatra & co.", sound.Title);
        Assert.Equal("/media/sounds/l'anatra_x1.mp3", Uri.UnescapeDataString(sound.SoundUrl.AbsolutePath));
    }

    [Fact]
    public void Skips_duplicates_and_sounds_hosted_elsewhere()
    {
        var sounds = MyinstantsParser.ParseList(ListPage);

        Assert.DoesNotContain(sounds, s => s.Title == "BRUH again");
        Assert.DoesNotContain(sounds, s => s.Title == "Elsewhere");
    }

    [Fact]
    public void A_page_without_results_gives_none() =>
        Assert.Empty(MyinstantsParser.ParseList("<html><body><p>Nessun risultato</p></body></html>"));

    [Fact]
    public void Reads_the_sound_of_a_sound_page()
    {
        const string page = """
            <meta property="og:title" content="Bruh meme - Pulsante sonoro"/>
            <meta property="og:audio" content="https://www.myinstants.com/media/sounds/movie_1_C2K5NH0.mp3"/>
            <h1 id="instant-page-title">Bruh meme</h1>
            <a href="/media/sounds/movie_1_C2K5NH0.mp3" download target="_blank">Scarica MP3</a>
            """;
        var pageUrl = new Uri("https://www.myinstants.com/it/instant/bruh-meme-44087/");

        var sound = MyinstantsParser.ParseSoundPage(page, pageUrl);

        Assert.NotNull(sound);
        Assert.Equal("Bruh meme", sound.Title);
        Assert.Equal("https://www.myinstants.com/media/sounds/movie_1_C2K5NH0.mp3", sound.SoundUrl.AbsoluteUri);
        Assert.Equal(pageUrl, sound.PageUrl);
    }

    [Fact]
    public void Falls_back_to_og_title_without_its_suffix()
    {
        const string page = """
            <meta content="https://www.myinstants.com/media/sounds/a.mp3" property="og:audio">
            <meta property="og:title" content="Vine boom - Pulsante sonoro">
            """;

        var sound = MyinstantsParser.ParseSoundPage(page, new Uri("https://www.myinstants.com/it/instant/vine-boom/"));

        Assert.Equal("Vine boom", sound?.Title);
    }

    [Fact]
    public void A_page_without_audio_gives_no_sound() =>
        Assert.Null(MyinstantsParser.ParseSoundPage("<h1 id=\"instant-page-title\">X</h1>", new Uri("https://www.myinstants.com/it/instant/x/")));

    [Theory]
    [InlineData("https://www.myinstants.com/it/instant/bruh-meme-44087/", "https://www.myinstants.com/it/instant/bruh-meme-44087/", null)]
    [InlineData("  www.myinstants.com/en/instant/vine-boom/?utm=x#top ", "https://www.myinstants.com/en/instant/vine-boom/", null)]
    [InlineData("http://myinstants.com/media/sounds/movie_1.mp3", null, "https://myinstants.com/media/sounds/movie_1.mp3")]
    [InlineData("https://www.myinstants.com/it/search/?name=bruh", null, null)]
    [InlineData("https://www.myinstants.com.evil.example/it/instant/x/", null, null)]
    [InlineData("ciao", null, null)]
    [InlineData("file:///C:/media/sounds/a.mp3", null, null)]
    public void Recognizes_links_to_sounds(string text, string? page, string? sound)
    {
        var (pageUrl, soundUrl) = MyinstantsParser.ClassifyLink(text);

        Assert.Equal(page, pageUrl?.AbsoluteUri);
        Assert.Equal(sound, soundUrl?.AbsoluteUri);
    }

    [Fact]
    public void Names_the_file_as_on_the_site()
    {
        var sound = new MyinstantsSound("x", new Uri("https://www.myinstants.com/media/sounds/movie_1_C2K5NH0.mp3"), null);

        Assert.Equal("movie_1_C2K5NH0.mp3", MyinstantsClient.FileNameOf(sound));
    }
}
