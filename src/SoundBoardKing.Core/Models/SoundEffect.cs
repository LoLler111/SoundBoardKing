using System.Text.Json.Serialization;

namespace SoundBoardKing.Core.Models;

/// <summary>A voice effect applied to the sounds while they play.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SoundEffect>))]
public enum SoundEffect
{
    None,

    /// <summary>Higher pitch, same speed.</summary>
    Chipmunk,

    /// <summary>Lower pitch, same speed.</summary>
    Deep,

    /// <summary>Metallic, ring-modulated voice.</summary>
    Robot,

    /// <summary>Thin, slightly distorted, like an old radio or a phone call.</summary>
    Radio,

    /// <summary>Repeats fading out behind the sound.</summary>
    Echo,
}
