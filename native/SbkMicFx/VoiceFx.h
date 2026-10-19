#pragma once

#include <windows.h>
#include <cstdint>
#include <vector>

namespace sbk
{
    // Same order as SoundBoardKing.Core.Models.SoundEffect: the app writes the number.
    enum class VoiceEffect : uint32_t
    {
        None = 0,
        Chipmunk = 1,
        Deep = 2,
        Robot = 3,
        Radio = 4,
        Echo = 5,
    };

    // Voice effects applied to the microphone itself. Prepare() allocates (LockForProcess);
    // Process() runs on the real-time thread: no locks, allocations or I/O.
    class VoiceFx
    {
    public:
        void Prepare(UINT32 sampleRate, UINT32 channels);

        // In place on interleaved float frames. amount is 0..1 like the app's knob.
        void Process(float* frames, UINT32 count, VoiceEffect effect, float amount);

    private:
        struct BiQuad
        {
            float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
            float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            float Run(float x)
            {
                const float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                return y;
            }
        };

        void Restart(VoiceEffect effect);
        void Pitch(float* frames, UINT32 count, float factor);
        void Robot(float* frames, UINT32 count, float wet);
        void Radio(float* frames, UINT32 count, float wet);
        void Echo(float* frames, UINT32 count, float amount);

        UINT32 m_rate = 48000;
        UINT32 m_channels = 1;
        VoiceEffect m_current = VoiceEffect::None;

        // Pitch: one delay line per channel read by two crossfaded taps sliding at the new speed.
        std::vector<float> m_pitchLine;   // channels * PitchLineFrames
        UINT32 m_pitchWrite = 0;
        double m_pitchPhase = 0;
        UINT32 m_window = 0;              // tap sweep in frames (~35 ms)

        double m_robotPhase = 0;

        std::vector<BiQuad> m_highPass;
        std::vector<BiQuad> m_lowPass;

        std::vector<float> m_echoLine;    // channels * echo delay
        UINT32 m_echoPos = 0;
    };
}
