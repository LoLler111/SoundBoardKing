#include "VoiceFx.h"

#include <algorithm>
#include <cmath>

namespace sbk
{
    namespace
    {
        constexpr double Pi = 3.14159265358979323846;
        constexpr UINT32 PitchLineFrames = 8192;   // power of two, > window at any common rate
        constexpr UINT32 MaxChannels = 16;

        // RBJ cookbook filters, Q 0.8 like the app's radio effect.
        void Design(float& b0, float& b1, float& b2, float& a1, float& a2, double rate, double hz, bool high)
        {
            const double w = 2 * Pi * hz / rate, cosw = std::cos(w), alpha = std::sin(w) / (2 * 0.8);
            const double a0 = 1 + alpha;
            const double k = high ? (1 + cosw) / 2 : (1 - cosw) / 2;
            b0 = static_cast<float>(k / a0);
            b1 = static_cast<float>((high ? -(1 + cosw) : (1 - cosw)) / a0);
            b2 = static_cast<float>(k / a0);
            a1 = static_cast<float>(-2 * cosw / a0);
            a2 = static_cast<float>((1 - alpha) / a0);
        }
    }

    void VoiceFx::Prepare(UINT32 sampleRate, UINT32 channels)
    {
        m_rate = sampleRate ? sampleRate : 48000;
        m_channels = (std::clamp)(channels, 1u, MaxChannels);
        m_window = (std::min)(static_cast<UINT32>(m_rate * 0.035), PitchLineFrames / 2);
        m_pitchLine.assign(static_cast<size_t>(PitchLineFrames) * m_channels, 0.0f);
        m_highPass.assign(m_channels, {});
        m_lowPass.assign(m_channels, {});
        for (UINT32 c = 0; c < m_channels; ++c)
        {
            auto& hp = m_highPass[c];
            Design(hp.b0, hp.b1, hp.b2, hp.a1, hp.a2, m_rate, 300, true);
            auto& lp = m_lowPass[c];
            Design(lp.b0, lp.b1, lp.b2, lp.a1, lp.a2, m_rate, 3400, false);
        }
        m_echoLine.assign(static_cast<size_t>(m_rate * 0.28) * m_channels, 0.0f);
        m_current = VoiceEffect::None;
        Restart(VoiceEffect::None);
    }

    // Switching effect starts from clean state, so nothing of the previous one leaks through.
    void VoiceFx::Restart(VoiceEffect effect)
    {
        m_current = effect;
        std::fill(m_pitchLine.begin(), m_pitchLine.end(), 0.0f);
        m_pitchWrite = 0;
        m_pitchPhase = 0;
        m_robotPhase = 0;
        for (auto& f : m_highPass) f.x1 = f.x2 = f.y1 = f.y2 = 0;
        for (auto& f : m_lowPass) f.x1 = f.x2 = f.y1 = f.y2 = 0;
        std::fill(m_echoLine.begin(), m_echoLine.end(), 0.0f);
        m_echoPos = 0;
    }

    void VoiceFx::Process(float* frames, UINT32 count, VoiceEffect effect, float amount)
    {
        if (m_pitchLine.empty())
            return; // not prepared
        if (effect != m_current)
            Restart(effect);
        amount = (std::clamp)(amount, 0.0f, 1.0f);

        switch (effect)
        {
        case VoiceEffect::Chipmunk: Pitch(frames, count, 1.0f + 0.65f * amount); break;
        case VoiceEffect::Deep: Pitch(frames, count, 1.0f - 0.4f * amount); break;
        case VoiceEffect::Robot: Robot(frames, count, amount); break;
        case VoiceEffect::Radio: Radio(frames, count, amount); break;
        case VoiceEffect::Echo: Echo(frames, count, amount); break;
        default: break;
        }
    }

    // Delay-line pitch shifter: two taps, half a window apart, slide through the recent past at
    // (1 - factor) frames per frame; each fades in and out (triangle) so the jumps are inaudible.
    // Adds up to one window (~35 ms) of latency.
    void VoiceFx::Pitch(float* frames, UINT32 count, float factor)
    {
        if (std::fabs(factor - 1.0f) < 0.005f)
            return; // at 1 the two taps would only comb-filter the voice
        constexpr UINT32 mask = PitchLineFrames - 1;
        const double window = m_window;
        const double step = (1.0 - factor) / window;

        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            for (UINT32 c = 0; c < m_channels; ++c)
                m_pitchLine[static_cast<size_t>(m_pitchWrite) * m_channels + c] = frame[c];

            double phases[2] = { m_pitchPhase, m_pitchPhase + 0.5 };
            if (phases[1] >= 1.0)
                phases[1] -= 1.0;
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                float out = 0;
                for (double phase : phases)
                {
                    const double delay = 2.0 + phase * window;  // frames behind the write head
                    const double read = m_pitchWrite - delay;
                    const double base = std::floor(read);
                    const float t = static_cast<float>(read - base);
                    const UINT32 i0 = static_cast<UINT32>(static_cast<int64_t>(base) & mask);
                    const UINT32 i1 = (i0 + 1) & mask;
                    const float a = m_pitchLine[static_cast<size_t>(i0) * m_channels + c];
                    const float b = m_pitchLine[static_cast<size_t>(i1) * m_channels + c];
                    const float gain = static_cast<float>(1.0 - std::fabs(2.0 * phase - 1.0));
                    out += (a + (b - a) * t) * gain;
                }
                frame[c] = out;
            }

            m_pitchPhase += step;
            if (m_pitchPhase >= 1.0) m_pitchPhase -= 1.0;
            if (m_pitchPhase < 0.0) m_pitchPhase += 1.0;
            m_pitchWrite = (m_pitchWrite + 1) & mask;
        }
    }

    // Ring modulation with a 55 Hz sine, like the sounds' robot.
    void VoiceFx::Robot(float* frames, UINT32 count, float wet)
    {
        const double step = 2 * Pi * 55.0 / m_rate;
        for (UINT32 i = 0; i < count; ++i)
        {
            const float carrier = static_cast<float>(std::sin(m_robotPhase)) * 1.4f;
            m_robotPhase += step;
            if (m_robotPhase > 2 * Pi)
                m_robotPhase -= 2 * Pi;
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            for (UINT32 c = 0; c < m_channels; ++c)
                frame[c] += (frame[c] * carrier - frame[c]) * wet;
        }
    }

    // Telephone band with saturation that grows with the amount.
    void VoiceFx::Radio(float* frames, UINT32 count, float wet)
    {
        const float drive = 1.0f + 3.0f * wet;
        const float level = 0.6f / std::tanh(drive * 0.5f + 0.5f);
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                const float band = m_lowPass[c].Run(m_highPass[c].Run(frame[c]));
                const float radio = std::tanh(band * drive) * level;
                frame[c] += (radio - frame[c]) * wet;
            }
        }
    }

    // 280 ms feedback delay: more amount, louder and longer repeats.
    void VoiceFx::Echo(float* frames, UINT32 count, float amount)
    {
        if (m_echoLine.empty())
            return;
        const float wet = 0.8f * amount;
        const float feedback = 0.2f + 0.4f * amount;
        const size_t length = m_echoLine.size();
        for (size_t i = 0; i < static_cast<size_t>(count) * m_channels; ++i)
        {
            const float delayed = m_echoLine[m_echoPos];
            const float dry = frames[i];
            m_echoLine[m_echoPos] = dry + delayed * feedback;
            frames[i] = dry + delayed * wet;
            if (++m_echoPos == length)
                m_echoPos = 0;
        }
    }
}
