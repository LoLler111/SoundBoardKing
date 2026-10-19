using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SoundBoardKing.Core.Myinstants;

/// <summary>A sound on Myinstants: its title, the mp3 and the page it comes from.</summary>
public sealed record MyinstantsSound(string Title, Uri SoundUrl, Uri? PageUrl);

/// <summary>
/// Reads the sounds out of Myinstants pages. There is no API: every result on a list page has a
/// button with <c>play('/media/sounds/x.mp3', 'loader-id', 'slug')</c> followed by a link to its
/// page, and a sound page carries the mp3 in <c>og:audio</c>.
/// </summary>
internal static partial class MyinstantsParser
{
    public static readonly Uri Site = new("https://www.myinstants.com/");

    public static IReadOnlyList<MyinstantsSound> ParseList(string html)
    {
        var sounds = new List<MyinstantsSound>();
        var seen = new HashSet<Uri>();
        // Each result starts with its own <div class="instant">: look for the button and the link in each one.
        var blocks = InstantBlock().Split(html);
        foreach (var block in blocks.Skip(1))
        {
            var play = PlayCall().Match(block);
            if (!play.Success || ToSoundUrl(DecodeJs(play.Groups["mp3"].Value)) is not { } soundUrl || !seen.Add(soundUrl))
                continue;

            var link = InstantLink().Match(block);
            var title = link.Success ? Clean(link.Groups["title"].Value) : "";
            if (title.Length == 0)
                title = TitleFromFile(soundUrl);
            var page = link.Success ? ToSiteUrl(WebUtility.HtmlDecode(link.Groups["href"].Value)) : null;
            sounds.Add(new MyinstantsSound(title, soundUrl, page));
        }
        return sounds;
    }

    /// <summary>The sound of a sound page; null when the page has none.</summary>
    public static MyinstantsSound? ParseSoundPage(string html, Uri pageUrl)
    {
        var audio = MetaContent(html, "og:audio");
        if (audio is null || ToSoundUrl(audio) is not { } soundUrl)
            return null;

        var heading = PageTitle().Match(html);
        var title = heading.Success ? Clean(heading.Groups["title"].Value) : "";
        if (title.Length == 0 && MetaContent(html, "og:title") is { } ogTitle)
            title = OgTitleSuffix().Replace(ogTitle, "").Trim();
        if (title.Length == 0)
            title = TitleFromFile(soundUrl);
        return new MyinstantsSound(title, soundUrl, pageUrl);
    }

    /// <summary>
    /// What a link pasted or dropped by the user points at: a sound page, an mp3, or nothing of
    /// Myinstants.
    /// </summary>
    public static (Uri? Page, Uri? Sound) ClassifyLink(string text)
    {
        text = text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !IsSiteHost(uri))
            return (null, null);
        if (ToSoundUrl(uri.AbsoluteUri) is { } sound)
            return (null, sound);
        if (uri.AbsolutePath.Contains("/instant/", StringComparison.OrdinalIgnoreCase))
            return (new UriBuilder(uri) { Scheme = "https", Port = -1, Query = "", Fragment = "" }.Uri, null);
        return (null, null);
    }

    /// <summary>An mp3 of the site, always over https; null for anything else.</summary>
    internal static Uri? ToSoundUrl(string value)
    {
        if (ToSiteUrl(value) is not { } uri)
            return null;
        return uri.AbsolutePath.StartsWith("/media/sounds/", StringComparison.OrdinalIgnoreCase) ? uri : null;
    }

    private static Uri? ToSiteUrl(string value)
    {
        if (!Uri.TryCreate(Site, value.Trim(), out var uri) || !IsSiteHost(uri))
            return null;
        return uri.Scheme == Uri.UriSchemeHttps ? uri : new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
    }

    internal static bool IsSiteHost(Uri uri) =>
        uri.Scheme is "http" or "https" &&
        (uri.Host.Equals("myinstants.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("www.myinstants.com", StringComparison.OrdinalIgnoreCase));

    private static string? MetaContent(string html, string property)
    {
        foreach (Match meta in MetaTag().Matches(html))
        {
            var tag = meta.Value;
            var name = Attribute(tag, "property") ?? Attribute(tag, "name");
            if (string.Equals(name, property, StringComparison.OrdinalIgnoreCase))
                return Attribute(tag, "content") is { } content ? WebUtility.HtmlDecode(content) : null;
        }
        return null;
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"""\b{name}\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static string Clean(string text) => Spaces().Replace(WebUtility.HtmlDecode(text), " ").Trim();

    private static string TitleFromFile(Uri soundUrl) =>
        Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(soundUrl.AbsolutePath)).Replace('_', ' ').Replace('-', ' ').Trim();

    // The arguments of play() are JavaScript strings inside an HTML attribute: undo both.
    private static string DecodeJs(string value)
    {
        value = WebUtility.HtmlDecode(value);
        return JsEscape().Replace(value, m => m.Groups["hex"].Success
            ? ((char)int.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString()
            : m.Groups["c"].Value);
    }

    [GeneratedRegex("""<div\s+class\s*=\s*["']instant["']""", RegexOptions.IgnoreCase)]
    private static partial Regex InstantBlock();

    [GeneratedRegex("""play\(\s*'(?<mp3>(?:[^'\\]|\\.)+)'""")]
    private static partial Regex PlayCall();

    [GeneratedRegex("""<a\s+href\s*=\s*"(?<href>[^"]+)"[^>]*class\s*=\s*"[^"]*instant-link[^"]*"[^>]*>(?<title>[^<]*)</a>""", RegexOptions.IgnoreCase)]
    private static partial Regex InstantLink();

    [GeneratedRegex("""<h1[^>]*id\s*=\s*["']instant-page-title["'][^>]*>(?<title>[^<]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex PageTitle();

    [GeneratedRegex("<meta\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex("""\s+-\s+[^-]+$""")]
    private static partial Regex OgTitleSuffix();

    [GeneratedRegex("""\\(?:u(?<hex>[0-9a-fA-F]{4})|(?<c>.))""")]
    private static partial Regex JsEscape();

    [GeneratedRegex("""\s+""")]
    private static partial Regex Spaces();
}
