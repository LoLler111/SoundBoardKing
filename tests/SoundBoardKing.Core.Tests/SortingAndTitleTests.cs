using SoundBoardKing.Core.Import;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Core.Tests;

public class TitleCleanerTests
{
    [Theory]
    [InlineData("01_bruh_sound_effect (1)", "Bruh sound effect")]
    [InlineData("airhorn", "Airhorn")]
    [InlineData("Applausi - Copia", "Applausi")]
    [InlineData("Applausi - Copia (2)", "Applausi")]
    [InlineData("risata copy (3)", "Risata")]
    [InlineData("3 - Sigla", "Sigla")]
    [InlineData("12. Il gallo", "Il gallo")]
    [InlineData("2 fast 2 furious", "2 fast 2 furious")]
    [InlineData("3.5 mm", "3.5 mm")]
    [InlineData("1-800-numero", "1-800-numero")]
    [InlineData("007 tema", "007 tema")]
    [InlineData("vine%20boom", "Vine boom")]
    [InlineData("  doppi   spazi  ", "Doppi spazi")]
    [InlineData("WOW", "WOW")]
    [InlineData("(1)", "(1)")]
    public void Cleans_file_names(string name, string expected) => Assert.Equal(expected, TitleCleaner.Clean(name));
}

public class SoundSorterTests
{
    private static readonly Sound A = new() { Title = "banana", DurationMs = 3000, AddedOn = new(2024, 1, 1), PlayCount = 2 };
    private static readonly Sound B = new() { Title = "Albicocca", DurationMs = 1000, AddedOn = new(2025, 1, 1), PlayCount = 9, LastPlayed = new(2025, 1, 1) };
    private static readonly Sound C = new() { Title = "ciliegia", DurationMs = 2000, AddedOn = new(2025, 1, 1), LastPlayed = new(2026, 1, 1) };

    private static string Sorted(SoundSort sort)
    {
        var sounds = new List<Sound> { A, B, C };
        SoundSorter.Apply(sounds, sort);
        return string.Concat(sounds.Select(s => s.Title[0]));
    }

    [Theory]
    [InlineData(SoundSort.Manual, "bAc")]
    [InlineData(SoundSort.Name, "Abc")]
    [InlineData(SoundSort.Newest, "cAb")] // same day: the one added later first
    [InlineData(SoundSort.MostPlayed, "Abc")]
    [InlineData(SoundSort.RecentlyPlayed, "cAb")]
    [InlineData(SoundSort.Shortest, "Acb")]
    [InlineData(SoundSort.Longest, "bcA")]
    public void Sorts_the_library(SoundSort sort, string expected) => Assert.Equal(expected, Sorted(sort));

    [Fact]
    public void Names_with_numbers_sort_like_explorer()
    {
        string[] titles = ["118_chiamate", "10himpertinente", "2 urla", "1_ska", "Applauso", "airhorn", "11heeh", "1eeh", "02 coro"];
        var sorted = titles.Order(Comparer<string>.Create(SoundSorter.CompareNatural)).ToArray();
        Assert.Equal(["1_ska", "1eeh", "02 coro", "2 urla", "10himpertinente", "11heeh", "118_chiamate", "airhorn", "Applauso"], sorted);
    }

    [Fact]
    public void Categories_follow_their_own_order_and_unknown_ids_go_last()
    {
        var library = new SoundLibrary { Sounds = [A, B, C], Sort = SoundSort.Shortest };
        var stray = Guid.NewGuid();
        var category = new Category { SoundIds = [stray, C.Id, A.Id], Sort = SoundSort.Name };
        library.Categories.Add(category);

        library.ApplySorts();

        Assert.Equal([B, C, A], library.Sounds);
        Assert.Equal([A.Id, C.Id, stray], category.SoundIds);
    }
}
