#pragma once

#include <windows.h>
#include "SharedRing.h"

namespace sbk
{
    // One APO instance's view of the shared ring. Open/Close run outside the real-time thread;
    // MixInto runs on it and therefore never blocks, allocates or does I/O.
    class MicReader
    {
    public:
        MicReader() = default;
        MicReader(const MicReader&) = delete;
        MicReader& operator=(const MicReader&) = delete;
        ~MicReader() { Close(); }

        // Creates the shared memory if no one has yet. Returns false (and the APO simply passes
        // the microphone through) when it cannot.
        bool Open();
        void Close();
        void Reset() { m_synced = false; }

        // Adds the producer's audio to an interleaved float buffer. Returns false when nothing
        // is attached, in which case dst is untouched.
        bool MixInto(float* dst, UINT32 frames, UINT32 channels, UINT32 deviceRate);

        // The effect the app wants on the voice, while the app is alive (fresh heartbeat).
        // Real-time safe. Returns false (no effect) otherwise.
        bool VoiceEffectWanted(uint32_t& effect, float& amount) const;

    private:
        HANDLE m_mapping = nullptr;
        SharedHeader* m_header = nullptr;
        float* m_ring = nullptr;
        int64_t m_pos = 0;
        double m_frac = 0.0;
        bool m_synced = false;
    };
}
