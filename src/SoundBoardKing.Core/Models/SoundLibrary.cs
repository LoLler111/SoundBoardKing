namespace SoundBoardKing.Core.Models;

public sealed class SoundLibrary
{
    public int Version { get; set; } = 1;
    public List<Sound> Sounds { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public Hotbar Hotbar { get; set; } = new();

    /// <summary>How <see cref="Sounds"/>, the "all sounds" list, is kept in order.</summary>
    public SoundSort Sort { get; set; }

    public Sound? FindSound(Guid id) => Sounds.Find(s => s.Id == id);

    /// <summary>All categories, depth first.</summary>
    public IEnumerable<Category> EnumerateCategories() => Categories.SelectMany(Walk);

    private static IEnumerable<Category> Walk(Category category) =>
        new[] { category }.Concat(category.Children.SelectMany(Walk));

    /// <summary>
    /// Puts every list back in its chosen order. The lists are stored sorted, so the phones show
    /// exactly what the PC shows without sorting anything themselves.
    /// </summary>
    public void ApplySorts()
    {
        SoundSorter.Apply(Sounds, Sort);
        var byId = Sounds.ToDictionary(s => s.Id);
        foreach (var category in EnumerateCategories())
            SoundSorter.Apply(category.SoundIds, category.Sort, byId.GetValueOrDefault);
    }

    /// <summary>Removes sounds from the library and from every category and hotbar slot that references them.</summary>
    public void RemoveSounds(IReadOnlySet<Guid> ids)
    {
        Sounds.RemoveAll(s => ids.Contains(s.Id));
        foreach (var category in EnumerateCategories())
            category.SoundIds.RemoveAll(ids.Contains);
        Hotbar.RemoveSounds(ids);
    }

    /// <summary>Removes a category (and its subcategories) wherever it sits in the tree.</summary>
    public bool RemoveCategory(Category category)
    {
        if (Categories.Remove(category))
            return true;
        var parent = EnumerateCategories().FirstOrDefault(c => c.Children.Contains(category));
        return parent?.Children.Remove(category) ?? false;
    }
}
