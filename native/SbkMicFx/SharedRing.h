#pragma once

#include <cstddef>
#include <cstdint>

// Shared memory between SoundBoardKing.exe (the single producer) and every SbkMicFx APO
// instance (readers) running inside audiodg.exe.
//
// The producer writes mono float samples, paced by its own clock, into a ring indexed by an
// absolute frame counter. Each reader keeps its own read position, so several APO instances
// (e.g. two apps capturing at once) all receive the same audio without coordinating.
//
// Keep in sync with src/SoundBoardKing.Audio/MicFx/MicFxLayout.cs.
namespace sbk
{
#ifdef SBK_TEST_SHARED_MEMORY_NAME
    // Test builds use a per-session name, which needs no special privilege to create.
    inline constexpr wchar_t SharedMemoryName[] = SBK_TEST_SHARED_MEMORY_NAME;
#else
    inline constexpr wchar_t SharedMemoryName[] = L"Global\\SoundBoardKing.MicFx.v1";
#endif
    inline constexpr uint32_t SharedMagic = 0x4D4B4253;   // "SBKM"
    inline constexpr uint32_t SharedVersion = 1;
    inline constexpr uint32_t RingFrames = 1u << 17;      // ~2.7 s at 48 kHz

#pragma pack(push, 8)
    struct SharedHeader
    {
        uint32_t magic;               // written last by the creator
        uint32_t version;
        uint32_t capacityFrames;      // always RingFrames
        uint32_t ringSampleRate;      // producer: sample rate of the ring contents
        int64_t writePos;             // producer: total frames written (release)
        int64_t producerHeartbeatMs;  // producer: GetTickCount64() of the last write
        int64_t readerHeartbeatMs;    // readers: GetTickCount64() of the last APOProcess
        uint32_t readerSampleRate;    // readers: microphone sample rate
        uint32_t readerChannels;      // readers: microphone channel count
        int32_t producerActive;       // producer: 1 while attached
        uint32_t reserved[3];
    };
#pragma pack(pop)

    static_assert(sizeof(SharedHeader) == 64, "layout shared with C#");
    static_assert(offsetof(SharedHeader, writePos) == 16, "layout shared with C#");
    static_assert(offsetof(SharedHeader, readerSampleRate) == 40, "layout shared with C#");
    static_assert(offsetof(SharedHeader, producerActive) == 48, "layout shared with C#");

    inline constexpr size_t SharedSize = sizeof(SharedHeader) + size_t{RingFrames} * sizeof(float);
}
