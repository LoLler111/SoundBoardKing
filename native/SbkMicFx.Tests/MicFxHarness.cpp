// Drives SbkMicFx the way audiodg.exe does: format negotiation, LockForProcess, then
// APOProcess every 10 ms on a silent 48 kHz stereo "microphone". A producer (the C# test)
// writes a 0.5-amplitude sine into the shared ring; the harness checks it shows up mixed in.
//
// Exit code 0 = pass. Usage: MicFxHarness.exe [seconds-to-wait-for-producer]

#include "../SbkMicFx/SbkMicFx.h"
#include "../SbkMicFx/VoiceFx.h"

#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <vector>

using namespace sbk;

namespace
{
    class FakeMediaType final : public IAudioMediaType
    {
    public:
        FakeMediaType(const GUID& subtype, UINT32 rate, UINT32 channels, UINT32 bytesPerSample)
        {
            m_format.guidFormatType = subtype;
            m_format.dwSamplesPerFrame = channels;
            m_format.dwBytesPerSampleContainer = bytesPerSample;
            m_format.dwValidBitsPerSample = bytesPerSample * 8;
            m_format.fFramesPerSecond = static_cast<FLOAT>(rate);
            m_format.dwChannelMask = channels == 2 ? KSAUDIO_SPEAKER_STEREO : KSAUDIO_SPEAKER_MONO;
        }

        STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IAudioMediaType))
            {
                *ppv = this;
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        STDMETHODIMP_(ULONG) AddRef() override { return 2; }
        STDMETHODIMP_(ULONG) Release() override { return 1; }
        STDMETHODIMP IsCompressedFormat(BOOL* compressed) override { *compressed = FALSE; return S_OK; }
        STDMETHODIMP IsEqual(IAudioMediaType*, DWORD* flags) override { *flags = 0; return S_FALSE; }
        STDMETHODIMP_(const WAVEFORMATEX*) GetAudioFormat() override { return nullptr; }
        STDMETHODIMP GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* format) override
        {
            *format = m_format;
            return S_OK;
        }

    private:
        UNCOMPRESSEDAUDIOFORMAT m_format{};
    };

    // Stands in for the audio engine's object that aggregates the APO.
    class FakeOuter final : public IUnknown
    {
    public:
        IUnknown* inner = nullptr;
        ULONG refs = 1;

        STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (riid == __uuidof(IUnknown))
            {
                *ppv = this;
                ++refs;
                return S_OK;
            }
            return inner->QueryInterface(riid, ppv);
        }
        STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
        STDMETHODIMP_(ULONG) Release() override { return --refs; }
    };

    int g_failures = 0;

    void Check(bool condition, const char* what)
    {
        std::printf("%s %s\n", condition ? "[ OK ]" : "[FAIL]", what);
        if (!condition)
            ++g_failures;
    }
}

int wmain(int argc, wchar_t** argv)
{
    const int waitSeconds = argc > 1 ? _wtoi(argv[1]) : 15;
    constexpr UINT32 Rate = 48000;
    constexpr UINT32 Channels = 2;
    constexpr UINT32 Frames = Rate / 100; // 10 ms periods, like the audio engine

    // Aggregation, as audiodg creates APOs: interfaces must delegate to the outer object.
    {
        FakeOuter outer;
        auto* aggregated = new SbkMicFx(&outer);
        outer.inner = aggregated->Inner();
        IAudioProcessingObject* apo = nullptr;
        const bool gotInterface = SUCCEEDED(outer.QueryInterface(__uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&apo)));
        const bool countsOnOuter = outer.refs == 2;
        IUnknown* identity = nullptr;
        const bool identityIsOuter = gotInterface && SUCCEEDED(apo->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void**>(&identity))) &&
                                     identity == &outer;
        if (identity)
            identity->Release();
        if (apo)
            apo->Release();
        Check(gotInterface && countsOnOuter && identityIsOuter && outer.refs == 1, "aggregazione COM (come la crea audiodg)");
        outer.inner->Release(); // destroys the aggregated object
    }

    auto* fx = new SbkMicFx();

    APO_REG_PROPERTIES* props = nullptr;
    Check(SUCCEEDED(fx->GetRegistrationProperties(&props)) && props->clsid == CLSID_SbkMicFx &&
              props->u32NumAPOInterfaces == 1 && props->iidAPOInterfaceList[0] == __uuidof(IAudioProcessingObject),
          "GetRegistrationProperties");
    CoTaskMemFree(props);

    IAudioSystemEffects2* effects = nullptr;
    Check(SUCCEEDED(fx->QueryInterface(__uuidof(IAudioSystemEffects2), reinterpret_cast<void**>(&effects))),
          "QueryInterface(IAudioSystemEffects2)");
    if (effects)
        effects->Release();

    FakeMediaType floatStereo(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, Rate, Channels, 4);
    FakeMediaType pcm16(KSDATAFORMAT_SUBTYPE_PCM, Rate, Channels, 2);
    FakeMediaType floatMono44(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, 44100, 1, 4);
    IAudioMediaType* supported = nullptr;
    Check(fx->IsInputFormatSupported(nullptr, &floatStereo, &supported) == S_OK && supported == &floatStereo,
          "accetta float 32 bit");
    Check(fx->IsInputFormatSupported(nullptr, &pcm16, &supported) == APOERR_FORMAT_NOT_SUPPORTED,
          "rifiuta PCM 16 bit");
    Check(fx->IsOutputFormatSupported(&floatStereo, &floatMono44, &supported) == S_FALSE && supported == &floatStereo,
          "propone il formato opposto quando differisce");

    std::vector<float> buffer(static_cast<size_t>(Frames) * Channels);
    APO_CONNECTION_DESCRIPTOR descriptor{};
    descriptor.Type = APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
    descriptor.pBuffer = reinterpret_cast<UINT_PTR>(buffer.data());
    descriptor.u32MaxFrameCount = Frames;
    descriptor.pFormat = &floatStereo;
    descriptor.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;
    APO_CONNECTION_DESCRIPTOR* descriptors[] = { &descriptor };

    IAudioProcessingObjectConfiguration* config = fx;
    IAudioProcessingObjectRT* rt = fx;
    Check(config->LockForProcess(1, descriptors, 1, descriptors) == S_OK, "LockForProcess");
    Check(config->LockForProcess(1, descriptors, 1, descriptors) == APOERR_APO_LOCKED, "doppio LockForProcess rifiutato");

    APO_CONNECTION_PROPERTY connection{};
    connection.pBuffer = reinterpret_cast<UINT_PTR>(buffer.data());
    connection.u32Signature = APO_CONNECTION_PROPERTY_SIGNATURE;
    APO_CONNECTION_PROPERTY* connections[] = { &connection };

    auto process = [&](bool silent, float fill) {
        for (float& sample : buffer)
            sample = fill;
        connection.u32ValidFrameCount = Frames;
        connection.u32BufferFlags = silent ? BUFFER_SILENT : BUFFER_VALID;
        rt->APOProcess(1, connections, 1, connections);
    };
    auto rms = [&]() {
        double sum = 0;
        for (float sample : buffer)
            sum += static_cast<double>(sample) * sample;
        return std::sqrt(sum / buffer.size());
    };

    // Nothing attached: a silent buffer stays silent and is flagged as such.
    process(true, 123.0f);
    Check(connection.u32BufferFlags == BUFFER_SILENT && rms() == 0.0, "senza app: silenzio intatto");
    process(false, 0.25f);
    Check(std::fabs(rms() - 0.25) < 1e-6, "senza app: microfono passa invariato");

    // Voice effects on their own: a 440 Hz sine through VoiceFx, frequency estimated from zero crossings.
    {
        auto run = [](VoiceEffect effect, float amount, std::vector<float>& out) {
            VoiceFx voice;
            voice.Prepare(48000, 1);
            out.assign(48000, 0.0f);
            for (size_t i = 0; i < out.size(); ++i)
                out[i] = 0.5f * static_cast<float>(std::sin(2 * 3.14159265358979 * 440 * i / 48000.0));
            for (size_t start = 0; start < out.size(); start += 480)
                voice.Process(out.data() + start, 480, effect, amount);
        };
        auto frequency = [](const std::vector<float>& s) {
            int crossings = 0;
            for (size_t i = 24001; i < s.size(); ++i)   // second half: past the start-up
                crossings += (s[i - 1] < 0) != (s[i] < 0);
            return crossings / 2.0 / 0.5;
        };
        std::vector<float> out, dry;
        run(VoiceEffect::None, 1, dry);
        run(VoiceEffect::Chipmunk, 1, out);
        const double up = frequency(out);
        run(VoiceEffect::Deep, 1, out);
        const double down = frequency(out);
        std::printf("voce: scoiattolo %.0f Hz, profonda %.0f Hz (da 440)\n", up, down);
        Check(up > 440 * 1.65 * 0.92 && up < 440 * 1.65 * 1.08, "voce: scoiattolo alza il tono di ~1.65");
        Check(down > 440 * 0.6 * 0.92 && down < 440 * 0.6 * 1.08, "voce: voce profonda abbassa il tono di ~0.6");
        run(VoiceEffect::Robot, 0, out);
        Check(out == dry, "voce: intensità 0 lascia la voce com'è");
        run(VoiceEffect::Robot, 1, out);
        Check(out != dry, "voce: il robot cambia la voce");

        VoiceFx echo;
        echo.Prepare(48000, 1);
        std::vector<float> impulse(48000, 0.0f);
        impulse[0] = 1.0f;
        for (size_t start = 0; start < impulse.size(); start += 480)
            echo.Process(impulse.data() + start, 480, VoiceEffect::Echo, 1);
        const size_t delay = static_cast<size_t>(48000 * 0.28);
        Check(std::fabs(impulse[delay]) > 0.5f, "voce: l'eco ripete dopo 280 ms");

        // The newer effects: each changes the voice more as the amount grows and stays in range
        // (underwater apart: its wobble shifts the phase, so it is checked by level and pitch below).
        auto distance = [&](const std::vector<float>& s) {
            double sum = 0;
            for (size_t i = 0; i < s.size(); ++i)
                sum += std::fabs(s[i] - dry[i]);
            return sum / s.size();
        };
        auto inRange = [](const std::vector<float>& s) {
            return std::all_of(s.begin(), s.end(), [](float x) { return std::isfinite(x) && std::fabs(x) <= 1.5f; });
        };
        const VoiceEffect newer[] = { VoiceEffect::Megaphone, VoiceEffect::Overdrive, VoiceEffect::Bitcrush,
                                      VoiceEffect::Muffled, VoiceEffect::Reverb, VoiceEffect::Chorus,
                                      VoiceEffect::Flanger, VoiceEffect::WalkieTalkie };
        const char* names[] = { "megafono", "distrutto", "8-bit", "dietro la porta", "cattedrale", "coro",
                                "jet", "walkie-talkie" };
        for (size_t e = 0; e < std::size(newer); ++e)
        {
            run(newer[e], 0.2f, out);
            const double soft = distance(out);
            run(newer[e], 1, out);
            const double hard = distance(out);
            const bool ok = inRange(out) && hard > soft && hard > 0.01;
            std::printf("voce: %s %.4f -> %.4f\n", names[e], soft, hard);
            Check(ok, "voce: il nuovo effetto cambia la voce con l'intensità");
        }
        run(VoiceEffect::Overdrive, 1, out);
        Check(std::all_of(out.begin(), out.end(), [](float x) { return std::fabs(x) <= 0.7f + 1e-6f; }),
              "voce: distrutto non supera 0.7");

        // Underwater: darker with more amount, and the cycles' length swings (~5% each way at full).
        auto rmsOf = [](const std::vector<float>& s) {
            double sum = 0;
            for (size_t i = s.size() / 2; i < s.size(); ++i)
                sum += static_cast<double>(s[i]) * s[i];
            return std::sqrt(sum / (s.size() - s.size() / 2));
        };
        auto swing = [](const std::vector<float>& s) {
            double last = -1, shortest = 1e9, longest = 0;
            for (size_t i = 4800; i < s.size(); ++i)   // past the first 100 ms
            {
                if (!(s[i - 1] < 0 && s[i] >= 0))
                    continue;
                const double at = (i - 1) + s[i - 1] / (s[i - 1] - s[i]);
                if (last >= 0)
                {
                    shortest = (std::min)(shortest, at - last);
                    longest = (std::max)(longest, at - last);
                }
                last = at;
            }
            return longest / shortest;
        };
        run(VoiceEffect::Underwater, 0, out);
        const double steady = swing(out);
        run(VoiceEffect::Underwater, 0.2f, out);
        const double soft = rmsOf(out);
        run(VoiceEffect::Underwater, 1, out);
        const double hard = rmsOf(out);
        const double wobbling = swing(out);
        std::printf("voce: sott'acqua livello %.3f -> %.3f, oscillazione %.3f -> %.3f\n", soft, hard, steady, wobbling);
        Check(inRange(out) && hard < soft * 0.7, "voce: sott'acqua scurisce la voce con l'intensità");
        Check(steady < 1.01 && wobbling > 1.05, "voce: sott'acqua fa ondeggiare il tono");

        run(VoiceEffect::Vibrato, 0, out);
        const double still = swing(out);
        run(VoiceEffect::Vibrato, 1, out);
        const double shaking = swing(out);
        std::printf("voce: pecora oscillazione %.3f -> %.3f\n", still, shaking);
        Check(inRange(out) && still < 1.01 && shaking > 1.06, "voce: pecora fa tremare il tono");

        // Demon: a much lower voice (0.65 x 440 Hz at the default amount) under the original (Goertzel).
        auto power = [](const std::vector<float>& s, double hz) {
            const double coefficient = 2 * std::cos(2 * 3.14159265358979 * hz / 48000);
            double s1 = 0, s2 = 0;
            for (size_t i = s.size() / 2; i < s.size(); ++i)
            {
                const double v = s[i] + coefficient * s1 - s2;
                s2 = s1;
                s1 = v;
            }
            const double n = static_cast<double>(s.size() - s.size() / 2);
            return (s1 * s1 + s2 * s2 - coefficient * s1 * s2) / (n * n);
        };
        // The delay-line shifter spreads it over lines ~10 Hz apart, so take the strongest around 286 Hz.
        auto lowest = [&](const std::vector<float>& s) {
            double best = 0;
            for (double hz = 260; hz <= 310; hz += 2)
                best = (std::max)(best, power(s, hz));
            return best;
        };
        run(VoiceEffect::Demon, 0.7f, out);
        std::printf("voce: demone 260-310 Hz %.2e (senza: %.2e)\n", lowest(out), lowest(dry));
        Check(inRange(out) && lowest(out) > 0.01 && power(out, 440) > power(dry, 440) * 0.05,
              "voce: demone aggiunge una voce molto più bassa");

        run(VoiceEffect::Alien, 1, out);
        Check(inRange(out) && distance(out) > 0.05, "voce: alieno cambia la voce");

        // Autotune: a 452 Hz tone (a bit sharp of A) comes out at 440 at full amount, untouched at none.
        auto runTone = [](VoiceEffect effect, float amount, double hz, double seconds, std::vector<float>& s) {
            VoiceFx voice;
            voice.Prepare(48000, 1);
            s.assign(static_cast<size_t>(48000 * seconds), 0.0f);
            for (size_t i = 0; i < s.size(); ++i)
                s[i] = 0.5f * static_cast<float>(std::sin(2 * 3.14159265358979 * hz * i / 48000.0));
            for (size_t start = 0; start + 480 <= s.size(); start += 480)
                voice.Process(s.data() + start, 480, effect, amount);
        };
        auto pitchOf = [](const std::vector<float>& s) {
            double first = -1, last = -1;
            int cycles = -1;
            for (size_t i = s.size() / 2; i < s.size(); ++i)
            {
                if (!(s[i - 1] < 0 && s[i] >= 0))
                    continue;
                last = (i - 1) + s[i - 1] / (s[i - 1] - s[i]);
                if (first < 0)
                    first = last;
                ++cycles;
            }
            return cycles > 0 ? 48000.0 * cycles / (last - first) : 0.0;
        };
        runTone(VoiceEffect::Autotune, 0, 452, 1, out);
        const double untouched = pitchOf(out);
        runTone(VoiceEffect::Autotune, 1, 452, 1, out);
        const double tuned = pitchOf(out);
        std::printf("voce: autotune 452 Hz -> %.1f (senza intensità %.1f)\n", tuned, untouched);
        Check(inRange(out) && std::fabs(untouched - 452) < 2.5 && std::fabs(tuned - 440) < 4.4,
              "voce: autotune porta il tono sulla nota");

        // Autotune on a voice-like tone (six harmonics gliding 180 -> 260 Hz with a 5 Hz vibrato): the
        // output stays within a few cents of the notes. Pitch tracked with a plain YIN over 2048 samples.
        {
            const size_t n = 48000 * 3;
            std::vector<float> voice(n);
            double phase = 0;
            for (size_t i = 0; i < n; ++i)
            {
                const double t = static_cast<double>(i) / 48000;
                phase += 2 * 3.14159265358979 * (180 + 80 * t / 3 + 4 * std::sin(2 * 3.14159265358979 * 5 * t)) / 48000;
                double v = 0;
                for (int h = 1; h <= 6; ++h)
                    v += std::sin(h * phase) / h;
                voice[i] = static_cast<float>(0.15 * v);
            }
            auto offKey = [](const std::vector<float>& s) {
                double sum = 0;
                int counted = 0;
                for (size_t at = 24000; at + 2048 <= s.size(); at += 960)
                {
                    const int maxLag = 48000 / 70, half = 2048 - maxLag;
                    std::vector<double> d(maxLag + 1, 1.0);
                    double running = 0;
                    for (int tau = 1; tau <= maxLag; ++tau)
                    {
                        double diff = 0;
                        for (int j = 0; j < half; ++j)
                        {
                            const double x = s[at + j] - s[at + j + tau];
                            diff += x * x;
                        }
                        running += diff;
                        d[tau] = running > 0 ? diff * tau / running : 1;
                    }
                    for (int tau = 48; tau < maxLag; ++tau)
                    {
                        if (d[tau] >= 0.15)
                            continue;
                        while (tau + 1 < maxLag && d[tau + 1] < d[tau])
                            ++tau;
                        const double note = 12 * std::log2(48000.0 / tau / 440);
                        sum += std::fabs(note - std::round(note)) * 100;
                        ++counted;
                        break;
                    }
                }
                return counted ? sum / counted : 100.0;
            };
            std::vector<float> tunedVoice = voice;
            VoiceFx tune;
            tune.Prepare(48000, 1);
            for (size_t start = 0; start + 480 <= n; start += 480)
                tune.Process(tunedVoice.data() + start, 480, VoiceEffect::Autotune, 0.7f);
            const double offFree = offKey(voice), offTuned = offKey(tunedVoice);
            std::printf("voce: autotune su voce che scivola %.1f cent dalla nota (senza: %.1f)\n", offTuned, offFree);
            Check(offFree > 20 && offTuned < 8, "voce: autotune tiene la voce sulle note");

            // At full amount only C, D, E, G and A come out (the glide passes F# G G# A A# B C).
            auto scaleShare = [](const std::vector<float>& s) {
                constexpr bool pentatonic[12] = { true, false, true, false, true, false, false, true, false, true, false, false };
                int inScale = 0, counted = 0;
                for (size_t at = 24000; at + 2048 <= s.size(); at += 960)
                {
                    // Plain YIN over 2048 samples, as above.
                    const int maxLag = 48000 / 70, half = 2048 - maxLag;
                    std::vector<double> d(maxLag + 1, 1.0);
                    double running = 0;
                    for (int tau = 1; tau <= maxLag; ++tau)
                    {
                        double diff = 0;
                        for (int j = 0; j < half; ++j)
                        {
                            const double x = s[at + j] - s[at + j + tau];
                            diff += x * x;
                        }
                        running += diff;
                        d[tau] = running > 0 ? diff * tau / running : 1;
                    }
                    for (int tau = 48; tau < maxLag; ++tau)
                    {
                        if (d[tau] >= 0.15)
                            continue;
                        while (tau + 1 < maxLag && d[tau + 1] < d[tau])
                            ++tau;
                        const int note = static_cast<int>(std::lround(12 * std::log2(48000.0 / tau / 440)));
                        inScale += pentatonic[((note - 3) % 12 + 12) % 12] ? 1 : 0;
                        ++counted;
                        break;
                    }
                }
                return counted ? static_cast<double>(inScale) / counted : 0.0;
            };
            std::vector<float> scaledVoice = voice;
            VoiceFx full;
            full.Prepare(48000, 1);
            for (size_t start = 0; start + 480 <= n; start += 480)
                full.Process(scaledVoice.data() + start, 480, VoiceEffect::Autotune, 1);
            const double share = scaleShare(scaledVoice);
            std::printf("voce: autotune al massimo %.0f%% delle note nella scala\n", share * 100);
            Check(share > 0.95, "voce: autotune al massimo salta fra le note della scala");
        }

        // Whisper: about the same level, the 440 Hz tone gone.
        run(VoiceEffect::Whisper, 1, out);
        std::printf("voce: sussurro livello %.3f (da %.3f), 440 Hz %.2e (da %.2e)\n", rmsOf(out), rmsOf(dry), power(out, 440), power(dry, 440));
        Check(inRange(out) && rmsOf(out) > rmsOf(dry) * 0.4 && rmsOf(out) < rmsOf(dry) * 1.6 && power(out, 440) < power(dry, 440) * 0.1,
              "voce: sussurro toglie il tono ma non le parole");

        // Vinyl: crackles over silence at full amount.
        runTone(VoiceEffect::Vinyl, 1, 0, 1, out);
        const auto clicks = std::count_if(out.begin(), out.end(), [](float x) { return std::fabs(x) > 0.03f; });
        Check(clicks > 20 && std::all_of(out.begin(), out.end(), [](float x) { return std::fabs(x) <= 0.35f; }),
              "voce: vinile crepita");
        run(VoiceEffect::Vinyl, 0.7f, out);
        Check(inRange(out) && distance(out) > 0.01, "voce: vinile cambia la voce");

        // Stutter: the start of each 0.6 s comes back over what follows; silence stays silence.
        run(VoiceEffect::Stutter, 1, out);
        const size_t piece = 48000 * 8 / 100;
        Check(inRange(out) && std::equal(out.begin() + piece + 200, out.begin() + piece + 300, dry.begin() + 200),
              "voce: balbuziente ripete il pezzo");
        runTone(VoiceEffect::Stutter, 1, 0, 1, out);
        Check(std::all_of(out.begin(), out.end(), [](float x) { return x == 0.0f; }), "voce: balbuziente tace sul silenzio");

        // Reverse is for sounds: the voice passes untouched.
        run(VoiceEffect::Reverse, 1, out);
        Check(out == dry, "voce: al contrario lascia la voce com'è");
    }

    std::printf("READY\n");
    std::fflush(stdout);

    // Wait for the producer, then measure one second of mixed audio.
    int waited = 0;
    double peakRms = 0;
    int mixedPeriods = 0;
    bool channelsMatch = true;
    for (int period = 0; period < waitSeconds * 100 && mixedPeriods < 100; ++period)
    {
        process(true, 0.0f);
        Sleep(10);
        if (connection.u32BufferFlags != BUFFER_VALID)
        {
            ++waited;
            continue;
        }
        const double value = rms();
        if (value > 0.01)
        {
            ++mixedPeriods;
            peakRms = value > peakRms ? value : peakRms;
            for (UINT32 i = 0; i < Frames; ++i)
                channelsMatch = channelsMatch && buffer[i * 2] == buffer[i * 2 + 1];
        }
    }

    std::printf("periodi in attesa: %d, periodi con audio: %d, RMS massimo: %.3f\n", waited, mixedPeriods, peakRms);
    Check(mixedPeriods >= 90, "l'audio del produttore arriva nel microfono");
    Check(peakRms > 0.30 && peakRms < 0.40, "livello corretto (sinusoide 0.5 -> RMS ~0.354)");
    Check(channelsMatch, "stesso segnale su entrambi i canali");

    // The app asks for the robot on the voice through the same shared memory (written by the C# test).
    {
        // Asked through the APO's own reader: a second opener in this process would lack the rights
        // that LOCAL SERVICE has inside audiodg.
        uint32_t voiceEffect = 0;
        float voiceAmount = 0;
        bool asked = false;
        for (int i = 0; i < 300 && !asked; ++i, Sleep(10))
            asked = fx->VoiceEffectWanted(voiceEffect, voiceAmount) && voiceEffect == 3;
        std::printf("voce chiesta dall'app: effetto %u, intensita %.2f\n", voiceEffect, voiceAmount);
        Check(asked && voiceAmount > 0.79f && voiceAmount < 0.81f, "il modulo riceve dall'app l'effetto sulla voce (robot, 80%)");
    }

    // Mixing on top of a real (non-silent) microphone signal must add, not replace.
    process(false, 0.1f);
    Check(connection.u32BufferFlags == BUFFER_VALID && rms() > 0.1, "si somma al segnale del microfono");

    Check(config->UnlockForProcess() == S_OK, "UnlockForProcess");
    Check(config->UnlockForProcess() == APOERR_ALREADY_UNLOCKED, "doppio UnlockForProcess rifiutato");
    fx->Release();

    std::printf(g_failures == 0 ? "PASS\n" : "FAIL (%d)\n", g_failures);
    return g_failures == 0 ? 0 : 1;
}
