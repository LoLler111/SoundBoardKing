namespace SoundBoardKing.Core.Models;

public sealed class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<Category> Children { get; set; } = [];

    /// <summary>Sounds in this category, in display order. A sound can belong to several categories.</summary>
    public List<Guid> SoundIds { get; set; } = [];

    /// <summary>How <see cref="SoundIds"/> is kept in order.</summary>
    public SoundSort Sort { get; set; }
}
