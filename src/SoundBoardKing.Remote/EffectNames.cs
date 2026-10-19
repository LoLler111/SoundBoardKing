using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Remote;

/// <summary>Wire names of the voice effects: "none", "chipmunk", "deep", "robot", "radio", "echo".</summary>
public static class EffectNames
{
    public static string ToWire(SoundEffect effect)
    {
        var name = effect.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static bool TryParse(string? text, out SoundEffect effect) =>
        Enum.TryParse(text, ignoreCase: true, out effect) && Enum.IsDefined(effect) && !int.TryParse(text, out _);
}
