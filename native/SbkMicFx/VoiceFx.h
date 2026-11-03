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
        Megaphone = 6,
        Overdrive = 7,
        Bitcrush = 8,
        Muffled = 9,
        Underwater = 10,
        Reverb = 11,
        Chorus = 12,
        Flanger = 13,
        Vibrato = 14,
        WalkieTalkie = 15,
        Demon = 16,
        Alien = 17,
        Autotune = 18,
        Whisper = 19,
        Vinyl = 20,
        Stutter = 21,
        Reverse = 22,   // sounds only: a live voice cannot be played backwards, so it passes clean
    };

    // Voice effects applied to the microphone itself. Prepare() allocates (LockForProcess);
    // Process() runs on the real-time thread: no locks, allocations or I/O.
    class VoiceFx
    {
    public:
        static constexpr int NoNote = INT32_MIN;   // the autotune has no note yet


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

        struct Comb
        {
            std::vector<float> line;
            UINT32 pos = 0;
            float store = 0;
        };

        struct AllPass
        {
            std::vector<float> line;
            UINT32 pos = 0;
        };

        void Restart(VoiceEffect effect);
        // dryMix > 0 adds the original voice back on top of the shifted one. Pitch() skips a factor of
        // about 1; PitchShift() always runs, for the autotune whose factor hovers around 1, and can take
        // a shorter window than the usual ~35 ms (less latency, so the correction lands on time).
        void Pitch(float* frames, UINT32 count, float factor, float dryMix = 0);
        void PitchShift(float* frames, UINT32 count, float factor, float dryMix, double windowSeconds = 0);
        void Robot(float* frames, UINT32 count, float wet);
        void Radio(float* frames, UINT32 count, float wet);
        void Echo(float* frames, UINT32 count, float amount);
        void Megaphone(float* frames, UINT32 count, float wet);
        void Overdrive(float* frames, UINT32 count, float wet);
        void Bitcrush(float* frames, UINT32 count, float amount);
        void Muffled(float* frames, UINT32 count, float amount);
        void Underwater(float* frames, UINT32 count, float amount);
        void Reverb(float* frames, UINT32 count, float amount);
        void Chorus(float* frames, UINT32 count, float wet);
        void Flanger(float* frames, UINT32 count, float wet);
        void Vibrato(float* frames, UINT32 count, float wet);
        void WalkieTalkie(float* frames, UINT32 count, float wet);
        void Demon(float* frames, UINT32 count, float wet);
        void Alien(float* frames, UINT32 count, float wet);
        void Autotune(float* frames, UINT32 count, float wet);
        void Whisper(float* frames, UINT32 count, float wet);
        void Vinyl(float* frames, UINT32 count, float amount);
        void Stutter(float* frames, UINT32 count, float wet);

        bool DetectPush(float sample);
        double DetectAnalyse();
        void Fft(bool inverse);
        void WhisperSlice(UINT32 channel);

        // The swinging delay line: Advance() to a new frame, set its samples, then read behind it.
        void ModAdvance() { m_modNewest = (m_modNewest + 1) & (ModLineFrames - 1); }
        float& ModAt(UINT32 channel) { return m_modLine[static_cast<size_t>(m_modNewest) * m_channels + channel]; }
        float ModRead(UINT32 channel, double delay) const;
        float Noise();

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

        std::vector<BiQuad> m_megaHighPass;
        std::vector<BiQuad> m_megaLowPass;
        std::vector<BiQuad> m_megaHonk;

        std::vector<BiQuad> m_bass;       // overdrive's low end, pushed up

        std::vector<float> m_crushHeld;   // one frame, held for several
        UINT32 m_crushLeft = 0;

        // Muffled and underwater: low-passes that follow the amount, redesigned when it changes.
        std::vector<BiQuad> m_muffleFirst;
        std::vector<BiQuad> m_muffleSecond;
        std::vector<BiQuad> m_waterLowPass;
        float m_designedAmount = -1;

        // Underwater, chorus, flanger and vibrato: a delay read at a swinging distance behind the newest frame.
        static constexpr UINT32 ModLineFrames = 8192;   // power of two, > 40 ms at any common rate
        std::vector<float> m_modLine;     // channels * ModLineFrames
        UINT32 m_modNewest = 0;
        double m_modPhase = 0;

        // Reverb: Freeverb's eight combs and four all-passes per channel.
        std::vector<Comb> m_combs;        // channels * 8
        std::vector<AllPass> m_allPasses; // channels * 4

        // Walkie-talkie: its ear on the voice, and the roger beep with static once it stops.
        float m_envelope = 0;
        bool m_talked = false;
        UINT32 m_quiet = 0;
        int64_t m_walkieTail = -1;        // frames into the tail, -1 when none plays
        uint32_t m_noise = 0x12345678;

        double m_alienCarrier = 0;
        double m_alienSweep = 0;

        // Autotune: YIN on a ~12 kHz mono copy (512 samples, analysed every 64), the factor gliding to the note.
        std::vector<float> m_detectWindow;
        std::vector<float> m_detectDifference;
        float m_detectSum = 0;
        UINT32 m_detectSummed = 0;
        UINT32 m_detectFilled = 0;
        UINT32 m_detectDecimate = 1;
        double m_detectRate = 12000;
        double m_detectHz = 0;
        UINT32 m_detectMisses = 0;
        int m_tuneNote = NoNote;          // semitones from A 440 the voice is held on
        float m_tuneTarget = 1;
        float m_tuneFactor = 1;

        // Whisper: overlapping ~10 ms slices with random phases, through one complex FFT.
        UINT32 m_whisperSize = 512;
        UINT32 m_whisperHop = 128;
        UINT32 m_whisperFilled = 0;
        std::vector<float> m_whisperWindow;
        std::vector<float> m_whisperHistory;   // channels * size, the last input samples
        std::vector<float> m_whisperOverlap;   // channels * size, output being added up
        std::vector<float> m_whisperReady;     // channels * hop, the next output samples
        std::vector<BiQuad> m_whisperAiry;
        std::vector<float> m_fftRe;
        std::vector<float> m_fftIm;
        std::vector<UINT32> m_fftReversed;
        std::vector<float> m_fftCos;
        std::vector<float> m_fftSin;

        // Vinyl: band filters, turntable wow and flutter, the crackle's current click.
        std::vector<BiQuad> m_vinylHighPass;
        std::vector<BiQuad> m_vinylLowPass;
        double m_wow = 0;
        double m_flutter = 0;
        float m_click = 0;

        // Stutter: the 80 ms piece caught at the start of each 0.6 s, repeated over the live voice.
        std::vector<float> m_stutterGrain;     // channels * grain frames
        UINT32 m_stutterAt = 0;                // frame within the period
        UINT32 m_stutterRepeats = 0;
        double m_stutterEnergy = 0;
    };
}
