using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Core.Tests;

public class SoundSearchTests
{
    [Theory]
    [InlineData("peter griffin", "peter-griffin")]
    [InlineData("peter griffin", "Peter_Griffin")]
    [InlineData("peter griffin", "PeterGriffin")]
    [InlineData("peter-griffin", "peter griffin")]
    [InlineData("griffin peter", "peter-griffin ride")]
    [InlineData("perche", "Perché no?!")]
    [InlineData("vine boom", "vine_boom_sound")]
    [InlineData("  BRUH  ", "bruh")]
    public void Finds_titles_whatever_the_symbols(string query, string title) =>
        Assert.True(new SoundSearch(query).Matches(title, ""));

    [Theory]
    [InlineData("peter stewie", "peter-griffin")]
    [InlineData("airhorn", "applausi")]
    public void Every_word_has_to_be_there(string query, string title) =>
        Assert.False(new SoundSearch(query).Matches(title, ""));

    [Fact]
    public void Words_can_be_in_the_title_or_the_artist() =>
        Assert.True(new SoundSearch("risata griffin").Matches("risata", "Peter_Griffin"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" - _ ")]
    public void Only_blanks_or_symbols_is_no_search(string query) => Assert.True(new SoundSearch(query).IsEmpty);
}
