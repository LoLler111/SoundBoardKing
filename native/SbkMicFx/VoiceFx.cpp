#include "VoiceFx.h"

#include <algorithm>
#include <cmath>
#include <iterator>

namespace sbk
{
    namespace
    {
        constexpr double Pi = 3.14159265358979323846;
        constexpr UINT32 PitchLineFrames = 8192;   // power of two, > window at any common rate
        constexpr UINT32 MaxChannels = 16;
        constexpr double WobbleHz = 2.5;
        constexpr double WobbleSeconds = 0.003;    // how far underwater's read head swings at full amount

        // Freeverb's tuning at 44.1 kHz; the right (odd) channels a little longer, for width.
        constexpr UINT32 CombLengths[] = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        constexpr UINT32 AllPassLengths[] = { 556, 441, 341, 225 };
        constexpr UINT32 Spread = 23;
        constexpr UINT32 Combs = static_cast<UINT32>(std::size(CombLengths));
        constexpr UINT32 AllPasses = static_cast<UINT32>(std::size(AllPassLengths));
        constexpr float Damping = 0.25f;
        constexpr float HallLevel = 2.5f;

        constexpr UINT32 DetectWindow = 512;       // the autotune's YIN, in decimated samples
        constexpr UINT32 DetectHop = 64;           // ~5 ms
        constexpr UINT32 DetectHold = 12;          // estimates without a tone (~60 ms) before letting go of the note
        constexpr double AutotuneWindowSeconds = 0.02;

        // From half the amount up only the C major pentatonic (C D E G A) counts: jumps of up to three
        // semitones, the stepping sound. Below, every semitone. Same as the sounds' Autotune class.
        constexpr float ScaleFrom = 0.5f;
        constexpr bool Pentatonic[12] = { true, false, true, false, true, false, false, true, false, true, false, false };
        // A pitch has to be this much nearer (in semitones) to another note before it leaves its own.
        constexpr double Stickiness = 0.2;

        bool Allowed(int note, bool scale)
        {
            return !scale || Pentatonic[((note - 3) % 12 + 12) % 12]; // C is 3 above A
        }

        int Nearest(double pitch, bool scale)
        {
            int best = VoiceFx::NoNote;
            for (int candidate = static_cast<int>(std::floor(pitch)) - 3; candidate <= static_cast<int>(std::ceil(pitch)) + 3; ++candidate)
            {
                if (Allowed(candidate, scale) && (best == VoiceFx::NoNote || std::fabs(pitch - candidate) < std::fabs(pitch - best)))
                    best = candidate;
            }
            return best;
        }
        constexpr double DetectThreshold = 0.15;

        // The sounds' StutterEffect.Repeats: extra plays of a loud piece, 1 to 3.
        UINT32 Repeats(float amount)
        {
            return amount <= 0 ? 0 : 1 + static_cast<UINT32>(std::lround(2 * amount));
        }

        // Advances a slow sine's phase (radians), wrapped so it never loses precision.
        double Step(double phase, double step)
        {
            phase += step;
            return phase > 2 * Pi ? phase - 2 * Pi : phase;
        }

        enum class Shape { LowPass, HighPass, Peak };

        // RBJ cookbook filters, the same NAudio's BiQuadFilter uses for the sounds; gainDb only for the peak.
        // Sets the coefficients only, so a filter redesigned while running keeps its state.
        template <typename Filter>
        void Design(Filter& f, double rate, double hz, double q, Shape shape, double gainDb = 0)
        {
            hz = (std::min)(hz, rate * 0.45);
            const double w = 2 * Pi * hz / rate, cosw = std::cos(w), alpha = std::sin(w) / (2 * q);
            double b0, b1, b2, a0 = 1 + alpha, a1 = -2 * cosw, a2 = 1 - alpha;
            switch (shape)
            {
            case Shape::HighPass:
                b0 = b2 = (1 + cosw) / 2;
                b1 = -(1 + cosw);
                break;
            case Shape::LowPass:
                b0 = b2 = (1 - cosw) / 2;
                b1 = 1 - cosw;
                break;
            default:
            {
                const double gain = std::pow(10.0, gainDb / 40);
                b0 = 1 + alpha * gain;
                b1 = -2 * cosw;
                b2 = 1 - alpha * gain;
                a0 = 1 + alpha / gain;
                a2 = 1 - alpha / gain;
                break;
            }
            }
            f.b0 = static_cast<float>(b0 / a0);
            f.b1 = static_cast<float>(b1 / a0);
            f.b2 = static_cast<float>(b2 / a0);
            f.a1 = static_cast<float>(a1 / a0);
            f.a2 = static_cast<float>(a2 / a0);
        }

        // Low-pass cutoff sliding from `from` Hz at amount 0 to `to` at 1, evenly by ear (like the app).
        double Cutoff(double from, double to, float amount)
        {
            return from * std::pow(to / from, static_cast<double>(amount));
        }

        template <typename Filter>
        void Clear(std::vector<Filter>& filters)
        {
            for (auto& f : filters)
                f.x1 = f.x2 = f.y1 = f.y2 = 0;
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
        m_megaHighPass.assign(m_channels, {});
        m_megaLowPass.assign(m_channels, {});
        m_megaHonk.assign(m_channels, {});
        m_bass.assign(m_channels, {});
        for (UINT32 c = 0; c < m_channels; ++c)
        {
            Design(m_highPass[c], m_rate, 300, 0.8, Shape::HighPass);
            Design(m_lowPass[c], m_rate, 3400, 0.8, Shape::LowPass);
            Design(m_megaHighPass[c], m_rate, 500, 0.8, Shape::HighPass);
            Design(m_megaLowPass[c], m_rate, 2500, 0.8, Shape::LowPass);
            Design(m_megaHonk[c], m_rate, 1500, 1.5, Shape::Peak, 9);
            Design(m_bass[c], m_rate, 150, 0.8, Shape::LowPass);
        }
        m_muffleFirst.assign(m_channels, {});
        m_muffleSecond.assign(m_channels, {});
        m_waterLowPass.assign(m_channels, {});
        m_crushHeld.assign(m_channels, 0.0f);
        m_modLine.assign(static_cast<size_t>(ModLineFrames) * m_channels, 0.0f);
        m_echoLine.assign(static_cast<size_t>(m_rate * 0.28) * m_channels, 0.0f);

        const double scale = m_rate / 44100.0;
        auto length = [&](UINT32 base, UINT32 channel) {
            return (std::max)(1u, static_cast<UINT32>((base + (channel % 2) * Spread) * scale));
        };
        m_combs.assign(static_cast<size_t>(m_channels) * Combs, {});
        m_allPasses.assign(static_cast<size_t>(m_channels) * AllPasses, {});
        for (UINT32 c = 0; c < m_channels; ++c)
        {
            for (UINT32 k = 0; k < Combs; ++k)
                m_combs[static_cast<size_t>(c) * Combs + k].line.assign(length(CombLengths[k], c), 0.0f);
            for (UINT32 k = 0; k < AllPasses; ++k)
                m_allPasses[static_cast<size_t>(c) * AllPasses + k].line.assign(length(AllPassLengths[k], c), 0.0f);
        }

        m_detectDecimate = (std::max)(1u, static_cast<UINT32>(std::lround(m_rate / 12000.0)));
        m_detectRate = static_cast<double>(m_rate) / m_detectDecimate;
        m_detectWindow.assign(DetectWindow, 0.0f);
        m_detectDifference.assign(DetectWindow / 2, 0.0f);

        // Power of two closest to 10.7 ms (512 at 48 kHz), like the sounds' whisper.
        m_whisperSize = 64;
        while (m_whisperSize * 2 <= m_rate * 0.0107 * 1.42)
            m_whisperSize *= 2;
        m_whisperHop = m_whisperSize / 4;
        m_whisperWindow.resize(m_whisperSize);
        for (UINT32 i = 0; i < m_whisperSize; ++i)
            m_whisperWindow[i] = static_cast<float>(0.5 - 0.5 * std::cos(2 * Pi * i / m_whisperSize));
        m_whisperHistory.assign(static_cast<size_t>(m_whisperSize) * m_channels, 0.0f);
        m_whisperOverlap.assign(static_cast<size_t>(m_whisperSize) * m_channels, 0.0f);
        m_whisperReady.assign(static_cast<size_t>(m_whisperHop) * m_channels, 0.0f);
        m_whisperAiry.assign(m_channels, {});
        m_vinylHighPass.assign(m_channels, {});
        m_vinylLowPass.assign(m_channels, {});
        for (UINT32 c = 0; c < m_channels; ++c)
        {
            Design(m_whisperAiry[c], m_rate, 250, 0.7, Shape::HighPass);
            Design(m_vinylHighPass[c], m_rate, 120, 0.707, Shape::HighPass);
        }
        m_fftRe.assign(m_whisperSize, 0.0f);
        m_fftIm.assign(m_whisperSize, 0.0f);
        m_fftReversed.resize(m_whisperSize);
        UINT32 bits = 0;
        while ((1u << bits) < m_whisperSize)
            ++bits;
        for (UINT32 i = 0; i < m_whisperSize; ++i)
        {
            UINT32 r = 0;
            for (UINT32 b = 0; b < bits; ++b)
                r |= ((i >> b) & 1u) << (bits - 1 - b);
            m_fftReversed[i] = r;
        }
        m_fftCos.resize(m_whisperSize / 2);
        m_fftSin.resize(m_whisperSize / 2);
        for (UINT32 i = 0; i < m_whisperSize / 2; ++i)
        {
            m_fftCos[i] = static_cast<float>(std::cos(2 * Pi * i / m_whisperSize));
            m_fftSin[i] = static_cast<float>(std::sin(2 * Pi * i / m_whisperSize));
        }

        m_stutterGrain.assign(static_cast<size_t>(m_rate * 0.08) * m_channels, 0.0f);
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
        Clear(m_highPass);
        Clear(m_lowPass);
        std::fill(m_echoLine.begin(), m_echoLine.end(), 0.0f);
        m_echoPos = 0;
        Clear(m_megaHighPass);
        Clear(m_megaLowPass);
        Clear(m_megaHonk);
        Clear(m_bass);
        m_crushLeft = 0;
        Clear(m_muffleFirst);
        Clear(m_muffleSecond);
        Clear(m_waterLowPass);
        m_designedAmount = -1;
        std::fill(m_modLine.begin(), m_modLine.end(), 0.0f);
        m_modNewest = 0;
        m_modPhase = 0;
        for (auto& comb : m_combs)
        {
            std::fill(comb.line.begin(), comb.line.end(), 0.0f);
            comb.pos = 0;
            comb.store = 0;
        }
        for (auto& allPass : m_allPasses)
        {
            std::fill(allPass.line.begin(), allPass.line.end(), 0.0f);
            allPass.pos = 0;
        }
        m_envelope = 0;
        m_talked = false;
        m_quiet = 0;
        m_walkieTail = -1;
        m_noise = 0x12345678;   // the sounds' effects start from the same seed
        m_alienCarrier = 0;
        m_alienSweep = 0;
        std::fill(m_detectWindow.begin(), m_detectWindow.end(), 0.0f);
        m_detectSum = 0;
        m_detectSummed = 0;
        m_detectFilled = 0;
        m_detectHz = 0;
        m_detectMisses = 0;
        m_tuneNote = NoNote;
        m_tuneTarget = 1;
        m_tuneFactor = 1;
        m_whisperFilled = 0;
        std::fill(m_whisperHistory.begin(), m_whisperHistory.end(), 0.0f);
        std::fill(m_whisperOverlap.begin(), m_whisperOverlap.end(), 0.0f);
        std::fill(m_whisperReady.begin(), m_whisperReady.end(), 0.0f);
        Clear(m_whisperAiry);
        Clear(m_vinylHighPass);
        Clear(m_vinylLowPass);
        m_wow = 0;
        m_flutter = 0;
        m_click = 0;
        m_stutterAt = 0;
        m_stutterRepeats = 0;
        m_stutterEnergy = 0;
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
        case VoiceEffect::Megaphone: Megaphone(frames, count, amount); break;
        case VoiceEffect::Overdrive: Overdrive(frames, count, amount); break;
        case VoiceEffect::Bitcrush: Bitcrush(frames, count, amount); break;
        case VoiceEffect::Muffled: Muffled(frames, count, amount); break;
        case VoiceEffect::Underwater: Underwater(frames, count, amount); break;
        case VoiceEffect::Reverb: Reverb(frames, count, amount); break;
        case VoiceEffect::Chorus: Chorus(frames, count, amount); break;
        case VoiceEffect::Flanger: Flanger(frames, count, amount); break;
        case VoiceEffect::Vibrato: Vibrato(frames, count, amount); break;
        case VoiceEffect::WalkieTalkie: WalkieTalkie(frames, count, amount); break;
        case VoiceEffect::Demon: Demon(frames, count, amount); break;
        case VoiceEffect::Alien: Alien(frames, count, amount); break;
        case VoiceEffect::Autotune: Autotune(frames, count, amount); break;
        case VoiceEffect::Whisper: Whisper(frames, count, amount); break;
        case VoiceEffect::Vinyl: Vinyl(frames, count, amount); break;
        case VoiceEffect::Stutter: Stutter(frames, count, amount); break;
        default: break; // Reverse among them: nothing to do for a live voice
        }
    }

    // Delay-line pitch shifter: two taps, half a window apart, slide through the recent past at
    // (1 - factor) frames per frame; each fades in and out (triangle) so the jumps are inaudible.
    // Adds up to one window (~35 ms) of latency.
    void VoiceFx::Pitch(float* frames, UINT32 count, float factor, float dryMix)
    {
        if (std::fabs(factor - 1.0f) < 0.005f)
            return; // at 1 the two taps would only comb-filter the voice
        PitchShift(frames, count, factor, dryMix);
    }

    void VoiceFx::PitchShift(float* frames, UINT32 count, float factor, float dryMix, double windowSeconds)
    {
        constexpr UINT32 mask = PitchLineFrames - 1;
        const double window = windowSeconds > 0 ? (std::min)(m_rate * windowSeconds, PitchLineFrames / 2.0) : m_window;
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
                frame[c] = out + frame[c] * dryMix;
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

    // Loudhailer: 500 Hz - 2.5 kHz honking at 1.5 kHz, driven harder than the radio.
    void VoiceFx::Megaphone(float* frames, UINT32 count, float wet)
    {
        const float drive = 2.0f + 6.0f * wet;
        const float level = 0.6f / std::tanh(drive * 0.4f);
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                const float band = m_megaHonk[c].Run(m_megaLowPass[c].Run(m_megaHighPass[c].Run(frame[c])));
                const float megaphone = std::tanh(band * drive) * level;
                frame[c] += (megaphone - frame[c]) * wet;
            }
        }
    }

    // Blown out: bass pushed up and the voice clipped flat, capped at 0.7 so it stays loud but not painful.
    void VoiceFx::Overdrive(float* frames, UINT32 count, float wet)
    {
        constexpr float Ceiling = 0.7f;
        const float gain = 1.0f + 16.0f * wet;
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                const float dry = frame[c];
                const float boosted = (dry + 3.0f * wet * m_bass[c].Run(dry)) * gain;
                const float blown = (std::clamp)(boosted, -1.0f, 1.0f) * Ceiling;
                frame[c] = dry + (blown - dry) * wet;
            }
        }
    }

    // 8-bit voice: from 48 kHz and 16 bits at no amount down to ~4.4 kHz and 4 bits at full.
    void VoiceFx::Bitcrush(float* frames, UINT32 count, float amount)
    {
        if (amount <= 0)
            return;
        const UINT32 hold = (std::max)(1u, static_cast<UINT32>(std::lround(m_rate * (1.0f + 10.0f * amount) / 48000.0f)));
        const float steps = std::pow(2.0f, 15.0f - 12.0f * amount);
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            if (m_crushLeft == 0)
            {
                for (UINT32 c = 0; c < m_channels; ++c)
                    m_crushHeld[c] = std::round(frame[c] * steps) / steps;
                m_crushLeft = hold;
            }
            --m_crushLeft;
            for (UINT32 c = 0; c < m_channels; ++c)
                frame[c] = m_crushHeld[c];
        }
    }

    // Behind a door: two low-pass stages, from 12 kHz at no amount down to 300 Hz at full, and a bit quieter.
    void VoiceFx::Muffled(float* frames, UINT32 count, float amount)
    {
        if (amount != m_designedAmount)
        {
            m_designedAmount = amount;
            const double hz = Cutoff(12000, 300, amount);
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                Design(m_muffleFirst[c], m_rate, hz, 0.707, Shape::LowPass);
                Design(m_muffleSecond[c], m_rate, hz, 0.707, Shape::LowPass);
            }
        }
        const float level = 1.0f - 0.4f * amount;
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            for (UINT32 c = 0; c < m_channels; ++c)
                frame[c] = m_muffleSecond[c].Run(m_muffleFirst[c].Run(frame[c])) * level;
        }
    }

    // Under water: a dark resonant low-pass (4 kHz down to 250 Hz) read back from a delay whose length
    // swings at 2.5 Hz, so the pitch wobbles. Adds at most 2 * 3 ms of latency.
    void VoiceFx::Underwater(float* frames, UINT32 count, float amount)
    {
        if (amount != m_designedAmount)
        {
            m_designedAmount = amount;
            const double hz = Cutoff(4000, 250, amount);
            for (UINT32 c = 0; c < m_channels; ++c)
                Design(m_waterLowPass[c], m_rate, hz, 1.2, Shape::LowPass);
        }
        const double depth = m_rate * WobbleSeconds * amount;
        const double step = 2 * Pi * WobbleHz / m_rate;
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            ModAdvance();
            for (UINT32 c = 0; c < m_channels; ++c)
                ModAt(c) = m_waterLowPass[c].Run(frame[c]);

            // One to 1 + 2 * depth frames behind what was just written.
            const double delay = 1.0 + depth * (1.0 + std::sin(m_modPhase));
            for (UINT32 c = 0; c < m_channels; ++c)
                frame[c] = ModRead(c, delay);
            m_modPhase = Step(m_modPhase, step);
        }
    }

    // The sample `delay` frames behind the newest (linear interpolation); at least 1 before the newest is set.
    float VoiceFx::ModRead(UINT32 channel, double delay) const
    {
        constexpr UINT32 mask = ModLineFrames - 1;
        const double read = m_modNewest - (std::clamp)(delay, 0.0, ModLineFrames - 2.0);
        const double base = std::floor(read);
        const float t = static_cast<float>(read - base);
        const UINT32 i0 = static_cast<UINT32>(static_cast<int64_t>(base) & mask);
        const UINT32 i1 = (i0 + 1) & mask;
        const float a = m_modLine[static_cast<size_t>(i0) * m_channels + channel];
        const float b = m_modLine[static_cast<size_t>(i1) * m_channels + channel];
        return a + (b - a) * t;
    }

    // A big hall: eight damped combs into four all-passes per channel. More amount, a longer and
    // louder hall (comb feedback 0.7 to 0.95).
    void VoiceFx::Reverb(float* frames, UINT32 count, float amount)
    {
        const float feedback = 0.7f + 0.25f * amount;
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                const float dry = frame[c];
                const float input = dry * 0.015f;
                float sum = 0;
                for (UINT32 k = 0; k < Combs; ++k)
                {
                    Comb& comb = m_combs[static_cast<size_t>(c) * Combs + k];
                    const float late = comb.line[comb.pos];
                    comb.store = late * (1.0f - Damping) + comb.store * Damping;
                    comb.line[comb.pos] = input + comb.store * feedback;
                    if (++comb.pos == comb.line.size())
                        comb.pos = 0;
                    sum += late;
                }
                for (UINT32 k = 0; k < AllPasses; ++k)
                {
                    AllPass& allPass = m_allPasses[static_cast<size_t>(c) * AllPasses + k];
                    const float stored = allPass.line[allPass.pos];
                    allPass.line[allPass.pos] = sum + stored * 0.5f;
                    if (++allPass.pos == allPass.line.size())
                        allPass.pos = 0;
                    sum = stored - sum;
                }
                frame[c] = dry * (1.0f - 0.3f * amount) + sum * HallLevel * amount;
            }
        }
    }

    // Three copies 20-30 ms late, each swinging ±2 ms at 0.8 Hz a third apart: several voices together.
    void VoiceFx::Chorus(float* frames, UINT32 count, float wet)
    {
        constexpr double BaseSeconds[] = { 0.020, 0.025, 0.030 };
        constexpr double SwingSeconds = 0.002;
        const double step = 2 * Pi * 0.8 / m_rate;
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            ModAdvance();
            for (UINT32 c = 0; c < m_channels; ++c)
                ModAt(c) = frame[c];
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                float voices = 0;
                for (size_t v = 0; v < std::size(BaseSeconds); ++v)
                {
                    const double swing = std::sin(m_modPhase + v * 2 * Pi / std::size(BaseSeconds));
                    voices += ModRead(c, m_rate * (BaseSeconds[v] + SwingSeconds * swing));
                }
                frame[c] = frame[c] * (1.0f - 0.4f * wet) + voices / std::size(BaseSeconds) * 0.9f * wet;
            }
            m_modPhase = Step(m_modPhase, step);
        }
    }

    // Jet: a copy 0.5-4.5 ms late sweeping at 0.25 Hz, fed back into itself; more amount, more feedback.
    void VoiceFx::Flanger(float* frames, UINT32 count, float wet)
    {
        const float feedback = 0.5f * wet;
        const double step = 2 * Pi * 0.25 / m_rate;
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            // Read before this frame is set: the newest slot still holds an old frame, so stay behind it.
            const double delay = 1.0 + m_rate * (0.0005 + 0.002 * (1.0 + std::sin(m_modPhase)));
            ModAdvance();
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                const float dry = frame[c];
                const float late = ModRead(c, delay);
                ModAt(c) = dry + late * feedback;
                frame[c] = (dry + late * wet) / (1.0f + 0.5f * wet);
            }
            m_modPhase = Step(m_modPhase, step);
        }
    }

    // Bleating: the pitch shaking at 6.5 Hz (up to ~5% each way) and the level dipping with it.
    void VoiceFx::Vibrato(float* frames, UINT32 count, float wet)
    {
        const double depth = m_rate * 0.0012 * wet;
        const double step = 2 * Pi * 6.5 / m_rate;
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            ModAdvance();
            for (UINT32 c = 0; c < m_channels; ++c)
                ModAt(c) = frame[c];
            const double swing = std::sin(m_modPhase);
            const float level = 1.0f - 0.35f * wet * static_cast<float>(1.0 + swing) / 2;
            for (UINT32 c = 0; c < m_channels; ++c)
                frame[c] = ModRead(c, 1.0 + depth * (1.0 + swing)) * level;
            m_modPhase = Step(m_modPhase, step);
        }
    }

    // White noise in -1..1 (xorshift), the same sequence as the sounds' walkie-talkie.
    float VoiceFx::Noise()
    {
        m_noise ^= m_noise << 13;
        m_noise ^= m_noise >> 17;
        m_noise ^= m_noise << 5;
        return static_cast<int32_t>(m_noise) / 2147483648.0f;
    }

    // Walkie-talkie: megaphone's band driven like a radio, hiss while the voice is on; a quarter of a
    // second after it stops, the roger beep (1.4 kHz, 80 ms) and a fading burst of static (150 ms).
    void VoiceFx::WalkieTalkie(float* frames, UINT32 count, float wet)
    {
        constexpr float Threshold = 0.02f;
        const float release = std::exp(-1.0f / (0.03f * m_rate));
        const UINT32 hang = m_rate / 4;
        const int64_t beep = static_cast<int64_t>(m_rate * 0.08);
        const int64_t gap = static_cast<int64_t>(m_rate * 0.02);
        const int64_t burst = static_cast<int64_t>(m_rate * 0.15);
        const float drive = 2.0f + 4.0f * wet;
        const float level = 0.6f / std::tanh(drive * 0.4f);
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            float peak = 0;
            for (UINT32 c = 0; c < m_channels; ++c)
                peak = (std::max)(peak, std::fabs(frame[c]));
            m_envelope = (std::max)(peak, m_envelope * release);
            if (m_envelope > Threshold)
            {
                m_talked = true;
                m_quiet = 0;
            }
            else if (m_talked && ++m_quiet > hang)
            {
                m_talked = false;
                m_walkieTail = 0;
            }
            const bool talking = m_talked && m_quiet == 0;

            float extra = 0;
            if (m_walkieTail >= 0)
            {
                const int64_t at = m_walkieTail++;
                if (m_walkieTail >= beep + gap + burst)
                    m_walkieTail = -1;
                if (at < beep)
                    extra = 0.2f * static_cast<float>(std::sin(2 * Pi * 1400.0 * at / m_rate));
                else if (at >= beep + gap)
                    extra = Noise() * 0.25f * (1.0f - static_cast<float>(at - beep - gap) / burst);
            }
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                const float dry = frame[c];
                const float band = m_megaLowPass[c].Run(m_megaHighPass[c].Run(dry));
                const float radio = std::tanh(band * drive) * level + (talking ? Noise() * 0.03f : 0.0f);
                frame[c] = dry + (radio - dry) * wet + extra * wet;
            }
        }
    }

    // Demon: the voice much lower (0.65 at the default amount), doubled with the original and growling.
    void VoiceFx::Demon(float* frames, UINT32 count, float wet)
    {
        Pitch(frames, count, 1.0f - 0.5f * wet, 0.6f * wet);
        const float drive = 1.0f + 3.0f * wet;
        const float level = 0.8f / std::tanh(drive);
        for (size_t i = 0; i < static_cast<size_t>(count) * m_channels; ++i)
            frames[i] += (std::tanh(frames[i] * drive) * level - frames[i]) * wet;
    }

    // Alien: a small pitch up, then a ring modulator whose carrier sweeps 200-800 Hz at 0.4 Hz.
    void VoiceFx::Alien(float* frames, UINT32 count, float wet)
    {
        Pitch(frames, count, 1.0f + 0.25f * wet);
        const float mix = 0.6f * wet;
        const double sweepStep = 2 * Pi * 0.4 / m_rate;
        for (UINT32 i = 0; i < count; ++i)
        {
            const float carrier = static_cast<float>(std::sin(m_alienCarrier)) * 1.4f;
            m_alienCarrier = Step(m_alienCarrier, 2 * Pi * (500.0 + 300.0 * std::sin(m_alienSweep)) / m_rate);
            m_alienSweep = Step(m_alienSweep, sweepStep);
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            for (UINT32 c = 0; c < m_channels; ++c)
                frame[c] += (frame[c] * carrier - frame[c]) * mix;
        }
    }

    // Takes one frame (channels averaged) into the pitch detector; true when a new estimate is ready.
    bool VoiceFx::DetectPush(float sample)
    {
        m_detectSum += sample;
        if (++m_detectSummed < m_detectDecimate)
            return false;
        m_detectWindow[DetectWindow - DetectHop + m_detectFilled] = m_detectSum / m_detectDecimate;
        m_detectSum = 0;
        m_detectSummed = 0;
        if (++m_detectFilled < DetectHop)
            return false;
        m_detectFilled = 0;
        m_detectHz = DetectAnalyse();
        std::copy(m_detectWindow.begin() + DetectHop, m_detectWindow.end(), m_detectWindow.begin());
        return true;
    }

    // YIN, 70-1000 Hz: the cumulative mean normalised difference dips towards 0 at the period. 0 for no
    // tone. Compares the newest half of the window with what came a lag before, so the estimate is fresh.
    double VoiceFx::DetectAnalyse()
    {
        constexpr UINT32 half = DetectWindow / 2;
        double energy = 0;
        for (UINT32 j = DetectWindow - half; j < DetectWindow; ++j)
            energy += static_cast<double>(m_detectWindow[j]) * m_detectWindow[j];
        if (std::sqrt(energy / half) < 0.005)
            return 0;

        const int tauMin = static_cast<int>(m_detectRate / 1000);
        const int tauMax = (std::min)(static_cast<int>(half) - 1, static_cast<int>(m_detectRate / 70));
        double running = 0;
        m_detectDifference[0] = 1;
        for (int tau = 1; tau <= tauMax; ++tau)
        {
            double d = 0;
            for (UINT32 j = DetectWindow - half; j < DetectWindow; ++j)
            {
                const double delta = static_cast<double>(m_detectWindow[j]) - m_detectWindow[j - tau];
                d += delta * delta;
            }
            running += d;
            m_detectDifference[tau] = running > 0 ? static_cast<float>(d * tau / running) : 1.0f;
        }
        int best = -1;
        for (int tau = (std::max)(2, tauMin); tau < tauMax; ++tau)
        {
            if (m_detectDifference[tau] >= DetectThreshold)
                continue;
            while (tau + 1 < tauMax && m_detectDifference[tau + 1] < m_detectDifference[tau])
                ++tau;
            best = tau;
            break;
        }
        if (best < 0)
            return 0;
        const double a = m_detectDifference[best - 1], b = m_detectDifference[best], c = m_detectDifference[best + 1];
        const double bend = a - 2 * b + c;
        const double shift = bend > 0 ? (a - c) / (2 * bend) : 0;
        return m_detectRate / (best + shift);
    }

    // Autotune: the pitch pulled to the nearest semitone (A = 440 Hz), or from half the amount to the
    // C major pentatonic; more amount, a stronger and quicker pull, from a slow natural glide to the
    // hard stepping snap. Same numbers as the sounds'.
    void VoiceFx::Autotune(float* frames, UINT32 count, float wet)
    {
        for (UINT32 i = 0; i < count; ++i)
        {
            const float* frame = frames + static_cast<size_t>(i) * m_channels;
            float mono = 0;
            for (UINT32 c = 0; c < m_channels; ++c)
                mono += frame[c];
            if (!DetectPush(mono / m_channels))
                continue;
            if (m_detectHz <= 0)
            {
                // A consonant or a breath keeps the note a moment; longer without a tone, back to the voice as it is.
                if (++m_detectMisses >= DetectHold)
                {
                    m_tuneTarget = 1;
                    m_tuneNote = NoNote;
                }
                continue;
            }
            m_detectMisses = 0;
            const double pitch = 12 * std::log2(m_detectHz / 440);   // semitones from A 440
            const bool scale = wet >= ScaleFrom;
            const int nearest = Nearest(pitch, scale);
            if (m_tuneNote == NoNote || !Allowed(m_tuneNote, scale) || std::fabs(pitch - nearest) + Stickiness < std::fabs(pitch - m_tuneNote))
                m_tuneNote = nearest;
            const double ratio = std::pow(2.0, (m_tuneNote - pitch) / 12);
            m_tuneTarget = static_cast<float>(std::pow(ratio, (std::min)(1.0f, 2.0f * wet)));
        }
        const double glide = 0.001 + 0.1 * (1 - wet) * (1 - wet) * (1 - wet);
        m_tuneFactor += (m_tuneTarget - m_tuneFactor) * static_cast<float>(1 - std::exp(-(count / (glide * m_rate))));
        PitchShift(frames, count, m_tuneFactor, 0, AutotuneWindowSeconds);
    }

    // In-place radix-2 complex FFT on m_fftRe/m_fftIm; the inverse divides by the size.
    void VoiceFx::Fft(bool inverse)
    {
        const UINT32 size = m_whisperSize;
        for (UINT32 i = 0; i < size; ++i)
        {
            const UINT32 j = m_fftReversed[i];
            if (j > i)
            {
                std::swap(m_fftRe[i], m_fftRe[j]);
                std::swap(m_fftIm[i], m_fftIm[j]);
            }
        }
        const float sign = inverse ? 1.0f : -1.0f;
        for (UINT32 length = 2; length <= size; length <<= 1)
        {
            const UINT32 half = length / 2, stride = size / length;
            for (UINT32 start = 0; start < size; start += length)
            {
                for (UINT32 k = 0; k < half; ++k)
                {
                    const float wr = m_fftCos[k * stride], wi = sign * m_fftSin[k * stride];
                    const UINT32 a = start + k, b = a + half;
                    const float tr = m_fftRe[b] * wr - m_fftIm[b] * wi, ti = m_fftRe[b] * wi + m_fftIm[b] * wr;
                    m_fftRe[b] = m_fftRe[a] - tr;
                    m_fftIm[b] = m_fftIm[a] - ti;
                    m_fftRe[a] += tr;
                    m_fftIm[a] += ti;
                }
            }
        }
        if (inverse)
        {
            for (UINT32 i = 0; i < size; ++i)
            {
                m_fftRe[i] /= size;
                m_fftIm[i] /= size;
            }
        }
    }

    // One whisper slice: keep the spectrum, randomise the phases, add it into the overlap.
    void VoiceFx::WhisperSlice(UINT32 channel)
    {
        const UINT32 size = m_whisperSize, hop = m_whisperHop;
        float* history = m_whisperHistory.data() + static_cast<size_t>(channel) * size;
        float* overlap = m_whisperOverlap.data() + static_cast<size_t>(channel) * size;
        for (UINT32 i = 0; i < size; ++i)
        {
            m_fftRe[i] = history[i] * m_whisperWindow[i];
            m_fftIm[i] = 0;
        }
        Fft(false);
        for (UINT32 k = 1; k < size / 2; ++k)
        {
            const float magnitude = std::sqrt(m_fftRe[k] * m_fftRe[k] + m_fftIm[k] * m_fftIm[k]);
            const float phase = Noise() * static_cast<float>(Pi);
            m_fftRe[k] = magnitude * std::cos(phase);
            m_fftIm[k] = magnitude * std::sin(phase);
            m_fftRe[size - k] = m_fftRe[k];
            m_fftIm[size - k] = -m_fftIm[k];
        }
        m_fftIm[0] = m_fftIm[size / 2] = 0;
        Fft(true);

        // Four Hann-squared windows overlap to 1.5: divided out here.
        for (UINT32 i = 0; i < size; ++i)
            overlap[i] += m_fftRe[i] * m_whisperWindow[i] / 1.5f;
        std::copy(overlap, overlap + hop, m_whisperReady.data() + static_cast<size_t>(channel) * hop);
        std::copy(overlap + hop, overlap + size, overlap);
        std::fill(overlap + size - hop, overlap + size, 0.0f);
        std::copy(history + hop, history + size, history);
    }

    // Whisper: the words stay, the tone goes; about 10 ms late, and airy through a 250 Hz high-pass.
    void VoiceFx::Whisper(float* frames, UINT32 count, float wet)
    {
        const UINT32 size = m_whisperSize, hop = m_whisperHop;
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            for (UINT32 c = 0; c < m_channels; ++c)
            {
                const float dry = frame[c];
                m_whisperHistory[static_cast<size_t>(c) * size + size - hop + m_whisperFilled] = dry;
                const float whisper = m_whisperAiry[c].Run(m_whisperReady[static_cast<size_t>(c) * hop + m_whisperFilled]);
                frame[c] = dry * (1.0f - wet) + whisper * 1.4f * wet;
            }
            if (++m_whisperFilled == hop)
            {
                m_whisperFilled = 0;
                for (UINT32 c = 0; c < m_channels; ++c)
                    WhisperSlice(c);
            }
        }
    }

    // Old record: 120 Hz up to 8 kHz at no amount (2.5 kHz at full), the turntable's slow wow and quick
    // flutter, and crackle and hiss growing with the amount. Same order of noise draws as the sounds'.
    void VoiceFx::Vinyl(float* frames, UINT32 count, float amount)
    {
        if (amount != m_designedAmount)
        {
            m_designedAmount = amount;
            const double hz = Cutoff(8000, 2500, amount);
            for (UINT32 c = 0; c < m_channels; ++c)
                Design(m_vinylLowPass[c], m_rate, hz, 0.707, Shape::LowPass);
        }
        const float clicksPerFrame = (2.0f + 30.0f * amount) / m_rate;
        const float clickDecay = std::exp(-1.0f / (0.0008f * m_rate));
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            ModAdvance();
            for (UINT32 c = 0; c < m_channels; ++c)
                ModAt(c) = m_vinylLowPass[c].Run(m_vinylHighPass[c].Run(frame[c]));
            const double delay = 1.0 + m_rate * (0.0015 * amount * (1.0 + std::sin(m_wow)) + 0.00012 * amount * (1.0 + std::sin(m_flutter)));
            m_wow = Step(m_wow, 2 * Pi * 0.55 / m_rate);
            m_flutter = Step(m_flutter, 2 * Pi * 7.0 / m_rate);

            if ((Noise() + 1) / 2 < clicksPerFrame)
            {
                const float sign = Noise() < 0 ? -1.0f : 1.0f;
                m_click = sign * (0.05f + 0.25f * std::fabs(Noise())) * amount;
            }
            const float crackle = m_click + Noise() * 0.004f * amount;
            m_click *= clickDecay;
            for (UINT32 c = 0; c < m_channels; ++c)
                frame[c] = ModRead(c, delay) + crackle;
        }
    }

    // Stutter: every 0.6 s an 80 ms piece is caught and, if loud, played 1 to 3 more times. A live voice
    // cannot fall behind, so the repeats cover what is said meanwhile; they crossfade in and out of the
    // voice over 3 ms and dip between themselves.
    void VoiceFx::Stutter(float* frames, UINT32 count, float wet)
    {
        const UINT32 grain = static_cast<UINT32>(m_stutterGrain.size() / m_channels);
        const UINT32 period = static_cast<UINT32>(m_rate * 0.6);
        const float fade = static_cast<float>((std::max)(1u, static_cast<UINT32>(m_rate * 0.003)));
        for (UINT32 i = 0; i < count; ++i)
        {
            float* frame = frames + static_cast<size_t>(i) * m_channels;
            const UINT32 at = m_stutterAt;
            if (at < grain)
            {
                for (UINT32 c = 0; c < m_channels; ++c)
                {
                    m_stutterGrain[static_cast<size_t>(at) * m_channels + c] = frame[c];
                    m_stutterEnergy += static_cast<double>(frame[c]) * frame[c];
                }
                if (at == grain - 1)
                {
                    const bool loud = std::sqrt(m_stutterEnergy / (static_cast<double>(grain) * m_channels)) > 0.02;
                    m_stutterRepeats = loud ? Repeats(wet) : 0;
                }
            }
            else if (at < grain + m_stutterRepeats * grain)
            {
                const UINT32 r = (at - grain) / grain, pos = (at - grain) % grain;
                const float in = (std::min)(1.0f, pos / fade);
                const float out = (std::min)(1.0f, (grain - pos) / fade);
                float live = 0;
                if (r == 0 && pos < fade)
                    live = 1.0f - pos / fade;
                else if (r == m_stutterRepeats - 1 && grain - pos < fade)
                    live = 1.0f - (grain - pos) / fade;
                const float gain = (std::min)(in, out);
                for (UINT32 c = 0; c < m_channels; ++c)
                    frame[c] = m_stutterGrain[static_cast<size_t>(pos) * m_channels + c] * gain + frame[c] * live;
            }
            if (++m_stutterAt == period)
            {
                m_stutterAt = 0;
                m_stutterEnergy = 0;
                m_stutterRepeats = 0;
            }
        }
    }
}
