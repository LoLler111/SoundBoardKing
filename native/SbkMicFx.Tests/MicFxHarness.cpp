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
