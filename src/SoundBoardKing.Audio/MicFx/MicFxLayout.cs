namespace SoundBoardKing.Audio.MicFx;

/// <summary>
/// Shared memory layout used with the SbkMicFx APO. Mirrors native/SbkMicFx/SharedRing.h byte for byte.
/// </summary>
internal static class MicFxLayout
{
    public const string SharedMemoryName = @"Global\SoundBoardKing.MicFx.v1";
    public const uint Magic = 0x4D4B4253; // "SBKM"
    public const uint Version = 1;
    public const int RingFrames = 1 << 17;
    public const int HeaderSize = 64;

    /// <summary>The header and the ring: what an older APO maps, and still enough to feed the microphone.</summary>
    public const long LegacySize = HeaderSize + (long)RingFrames * sizeof(float);

    // The voice tap after the ring: the voice as the others hear it, written by the APO for the meter.
    public const int VoiceTapFrames = 1 << 13;
    public const long VoiceTapOffset = LegacySize;
    public const int VoiceTapWritePosOffset = 0;
    public const int VoiceTapHeartbeatOffset = 8;
    public const int VoiceTapSampleRateOffset = 24;
    public const int VoiceTapHeaderSize = 32;
    public const long Size = VoiceTapOffset + VoiceTapHeaderSize + (long)VoiceTapFrames * sizeof(float);

    public const int MagicOffset = 0;
    public const int VersionOffset = 4;
    public const int CapacityOffset = 8;
    public const int RingSampleRateOffset = 12;
    public const int WritePosOffset = 16;
    public const int ProducerHeartbeatOffset = 24;
    public const int ReaderHeartbeatOffset = 32;
    public const int ReaderSampleRateOffset = 40;
    public const int ReaderChannelsOffset = 44;
    public const int ProducerActiveOffset = 48;
    public const int VoiceEffectOffset = 52;
    public const int VoiceAmountOffset = 56;
    public const int VoiceHeartbeatOffset = 60;
}
