using System.Text.Json.Serialization;

namespace SoundBoardKing.Core.Models;

/// <summary>What the voice effect is applied to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EffectTarget>))]
public enum EffectTarget
{
    /// <summary>Only the sounds started from the soundboard.</summary>
    Sounds,

    /// <summary>Only the user's own voice in the microphone.</summary>
    Voice,

    Both,

    /// <summary>Effect switched off everywhere.</summary>
    Off,
}
