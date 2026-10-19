namespace SoundBoardKing.Core.Models;

public sealed class SoundLibrary
{
    public int Version { get; set; } = 1;
    public List<Sound> Sounds { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public Hotbar Hotbar { get; set; } = new();

    /// <summary>How <see cref="Sounds"/>, the "all sounds" list, is kept in order.</summary>
    public SoundSort Sort { get; set; }

    /// <summary>
    /// Files of the sounds folder removed from the library on purpose, relative to that folder:
    /// looking for new sounds there skips them instead of adding them back.
    /// </summary>
    public List<string> ExcludedFiles { get; set; } = [];

    public const string UncategorizedName = "Senza categoria";

    public Sound? FindSound(Guid id) => Sounds.Find(s => s.Id == id);

    /// <summary>
    /// Puts "Senza categoria" first among the categories (creating it the first time) and makes it
    /// hold exactly the sounds that are in no other category: the ones still there keep their place,
    /// new ones go at the end. Returns true when the category tree itself changed.
    /// </summary>
    public bool SyncUncategorized()
    {
        var target = Categories.FirstOrDefault(c => c.IsUncategorized);
        bool treeChanged = target is null || Categories[0] != target;
        if (target is null)
            target = new Category { Name = UncategorizedName, Icon = "S", IsUncategorized = true };
        else
            Categories.Remove(target);
        Categories.Insert(0, target);

        var categorized = EnumerateCategories()
            .Where(c => c != target)
            .SelectMany(c => c.SoundIds)
            .ToHashSet();
        var inLibrary = Sounds.Select(s => s.Id).ToHashSet();
        target.SoundIds.RemoveAll(id => categorized.Contains(id) || !inLibrary.Contains(id));
        var listed = target.SoundIds.ToHashSet();
        target.SoundIds.AddRange(Sounds.Select(s => s.Id).Where(id => !categorized.Contains(id) && !listed.Contains(id)));
        return treeChanged;
    }

    /// <summary>
    /// For every sound, the category it is in; with several, the most specific (deepest) one,
    /// the first in the tree among equals.
    /// </summary>
    public Dictionary<Guid, Category> CategoryOfSounds()
    {
        var result = new Dictionary<Guid, (Category category, int depth)>();
        void Visit(Category category, int depth)
        {
            foreach (var id in category.SoundIds)
            {
                if (!result.TryGetValue(id, out var found) || depth > found.depth)
                    result[id] = (category, depth);
            }
            foreach (var child in category.Children)
                Visit(child, depth + 1);
        }
        foreach (var category in Categories)
            Visit(category, 0);
        return result.ToDictionary(p => p.Key, p => p.Value.category);
    }

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
