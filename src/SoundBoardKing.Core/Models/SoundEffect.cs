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

    /// <summary>Narrow, honking and driven, like a loudhailer.</summary>
    Megaphone,

    /// <summary>Boomy bass and hard clipping: the blown-out "earrape" sound, with a ceiling.</summary>
    Overdrive,

    /// <summary>Fewer samples and fewer levels: an 8-bit console voice.</summary>
    Bitcrush,

    /// <summary>Dull and quieter, like someone behind a door.</summary>
    Muffled,

    /// <summary>Dark, resonant and wobbling, like talking under water.</summary>
    Underwater,

    /// <summary>A big hall ringing on after the sound.</summary>
    Reverb,

    /// <summary>A few slightly detuned copies together, like several people.</summary>
    Chorus,

    /// <summary>Sweeping, metallic whoosh, like a jet passing.</summary>
    Flanger,

    /// <summary>Quick shaking of pitch and level: a bleating voice.</summary>
    Vibrato,

    /// <summary>Radio band with hiss, a roger beep and a burst of static when the voice stops.</summary>
    WalkieTalkie,

    /// <summary>Much lower, doubled with the original and growling.</summary>
    Demon,

    /// <summary>A little higher, through a sweeping ring modulator.</summary>
    Alien,

    /// <summary>The pitch pulled to the nearest note; at full amount the hard, stepping pop-song sound.</summary>
    Autotune,

    /// <summary>The voice turned into breath: same words, no tone.</summary>
    Whisper,

    /// <summary>An old record: narrow, wobbling, with crackle and hiss.</summary>
    Vinyl,

    /// <summary>Little pieces repeated: "b-b-b-buongiorno".</summary>
    Stutter,

    /// <summary>The sound played backwards; sounds only, the microphone passes the voice clean.</summary>
    Reverse,
}
