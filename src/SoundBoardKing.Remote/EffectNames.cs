using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Remote;

/// <summary>
/// Wire names of the voice effects: "none", "chipmunk", "deep", "robot", "radio", "echo",
/// "megaphone", "overdrive", "bitcrush", "muffled", "underwater", "reverb", "chorus", "flanger",
/// "vibrato", "walkieTalkie", "demon", "alien", "autotune", "whisper", "vinyl", "stutter", "reverse".
/// </summary>
public static class EffectNames
{
    public static string ToWire(SoundEffect effect)
    {
        var name = effect.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static bool TryParse(string? text, out SoundEffect effect) =>
        Enum.TryParse(text, ignoreCase: true, out effect) && Enum.IsDefined(effect) && !int.TryParse(text, out _);

    /// <summary>"sounds", "voice", "both" or "off".</summary>
    public static string ToWire(EffectTarget target) => target.ToString().ToLowerInvariant();

    public static bool TryParseTarget(string? text, out EffectTarget target) =>
        Enum.TryParse(text, ignoreCase: true, out target) && Enum.IsDefined(target) && !int.TryParse(text, out _);
}
