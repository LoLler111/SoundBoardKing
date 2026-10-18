namespace SoundBoardKing.Core.Models;

/// <summary>Where a sound goes when it starts.</summary>
public enum PlayTarget
{
    /// <summary>Headphones/speakers, plus the microphone when it is switched on.</summary>
    Default,

    /// <summary>Preview: only in the headphones/speakers, never in the microphone.</summary>
    Speakers,

    /// <summary>Only in the microphone, even when it is switched off; nothing in the headphones.</summary>
    Mic,
}
