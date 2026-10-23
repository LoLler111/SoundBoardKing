using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.Core.Tests;

public class MissingFilesTests
{
    [Fact]
    public void Finds_the_missing_files_in_a_listed_folder()
    {
        using var temp = new TempDirectory();
        var present = Enumerable.Range(0, MissingFiles.ListFolderFrom)
            .Select(i => temp.CreateFile($@"Suoni\suono{i}.mp3"))
            .ToList();
        var gone = Path.Combine(temp.Path, "Suoni", "sparito.mp3");

        var missing = MissingFiles.Find([.. present, gone, present[0].ToUpperInvariant()]);

        Assert.Equal([gone], missing);
    }

    [Fact]
    public void Checks_the_files_of_a_folder_with_few_sounds_one_by_one()
    {
        using var temp = new TempDirectory();
        var present = temp.CreateFile(@"Download\suono.mp3");
        var gone = Path.Combine(temp.Path, "Download", "sparito.mp3");

        Assert.Equal([gone], MissingFiles.Find([present, gone]));
    }

    [Fact]
    public void Every_file_of_a_folder_that_is_gone_is_missing()
    {
        using var temp = new TempDirectory();
        var paths = Enumerable.Range(0, MissingFiles.ListFolderFrom)
            .Select(i => Path.Combine(temp.Path, "Sparita", $"suono{i}.mp3"))
            .ToList();

        Assert.Equal(paths.Count, MissingFiles.Find(paths).Count);
    }

    [Fact]
    public void A_path_that_is_not_valid_counts_as_missing()
    {
        Assert.Equal(["", "a\0b.mp3"], MissingFiles.Find(["", "a\0b.mp3"]).Order());
    }
}
