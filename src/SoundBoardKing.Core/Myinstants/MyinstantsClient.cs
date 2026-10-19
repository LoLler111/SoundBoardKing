using System.Net;

namespace SoundBoardKing.Core.Myinstants;

/// <summary>One page of results; <see cref="HasMore"/> when the next page may have more.</summary>
public sealed record MyinstantsPage(IReadOnlyList<MyinstantsSound> Sounds, bool HasMore);

/// <summary>
/// Searches Myinstants and downloads its sounds, one request for each thing the user does: no
/// crawling, no cache on disk. Pages are read in Italian; the results are the same in any language.
/// </summary>
public sealed class MyinstantsClient(HttpClient http)
{
    /// <summary>The site's sounds are a few hundred KB; anything bigger than this is not one of them.</summary>
    public const int MaxSoundBytes = 20 * 1024 * 1024;

    // A full page on the site; fewer means it was the last one.
    private const int PageSize = 36;

    /// <summary>Results for <paramref name="query"/>; an empty query gives the sounds trending in Italy.</summary>
    public async Task<MyinstantsPage> SearchAsync(string query, int page = 1, CancellationToken cancel = default)
    {
        query = query.Trim();
        var path = query.Length == 0
            ? "/it/index/it/"
            : $"/it/search/?name={Uri.EscapeDataString(query)}";
        if (page > 1)
            path += (path.Contains('?') ? "&" : "?") + $"page={page}";

        using var response = await http.GetAsync(new Uri(MyinstantsParser.Site, path), cancel).ConfigureAwait(false);
        // With no results, or past the last page, the site answers 404.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new MyinstantsPage([], false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        var sounds = MyinstantsParser.ParseList(html);
        return new MyinstantsPage(sounds, sounds.Count >= PageSize);
    }

    /// <summary>True when the text is a link to a sound of the site (its page or its mp3).</summary>
    public static bool IsSoundLink(string text)
    {
        var (page, sound) = MyinstantsParser.ClassifyLink(text);
        return page is not null || sound is not null;
    }

    /// <summary>The sound a pasted link points at; null when it is not a Myinstants sound.</summary>
    public async Task<MyinstantsSound?> ResolveAsync(string link, CancellationToken cancel = default)
    {
        var (page, sound) = MyinstantsParser.ClassifyLink(link);
        if (sound is not null)
            return new MyinstantsSound(TitleOf(sound), sound, null);
        if (page is null)
            return null;

        var html = await http.GetStringAsync(page, cancel).ConfigureAwait(false);
        return MyinstantsParser.ParseSoundPage(html, page);
    }

    /// <summary>The mp3, in memory: the preview plays it from here and only "add" writes it to disk.</summary>
    /// <exception cref="InvalidDataException">The file is too big to be a sound of the site.</exception>
    public async Task<byte[]> DownloadAsync(MyinstantsSound sound, CancellationToken cancel = default)
    {
        // Only the site's own media: a page cannot send the app elsewhere.
        if (MyinstantsParser.ToSoundUrl(sound.SoundUrl.AbsoluteUri) is not { } url)
            throw new InvalidDataException("Il link non è un suono di Myinstants.");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxSoundBytes)
            throw new InvalidDataException("Il file è troppo grande.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxSoundBytes)
                throw new InvalidDataException("Il file è troppo grande.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>The name the mp3 has on the site, e.g. "movie_1_C2K5NH0.mp3": unique there, so it is unique here too.</summary>
    public static string FileNameOf(MyinstantsSound sound)
    {
        var name = Path.GetFileName(Uri.UnescapeDataString(sound.SoundUrl.AbsolutePath));
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name.Length == 0 ? "suono.mp3" : name;
    }

    private static string TitleOf(Uri sound) =>
        Path.GetFileNameWithoutExtension(FileNameOf(new MyinstantsSound("", sound, null))).Replace('_', ' ').Trim();
}
