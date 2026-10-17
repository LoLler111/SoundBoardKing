namespace SoundBoardKing.Core.Storage;

/// <summary>A phone allowed to control this PC. Only a hash of its token is kept.</summary>
public sealed class PairedDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Hex SHA-256 of the token handed to the phone at pairing time.</summary>
    public string TokenHash { get; set; } = "";

    public DateTime PairedOn { get; set; } = DateTime.UtcNow;
}
