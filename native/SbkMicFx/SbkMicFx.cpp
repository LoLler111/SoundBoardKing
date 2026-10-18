#include "SbkMicFx.h"

#include <initguid.h>
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <cstdio>

#include "Log.h"

namespace sbk
{
    std::atomic<long> g_objectCount{ 0 };
    std::atomic<long> g_lockCount{ 0 };

    namespace
    {
        const wchar_t* ModeName(const GUID& mode)
        {
            if (mode == AUDIO_SIGNALPROCESSINGMODE_DEFAULT) return L"predefinita";
            if (mode == AUDIO_SIGNALPROCESSINGMODE_RAW) return L"raw";
            if (mode == AUDIO_SIGNALPROCESSINGMODE_COMMUNICATIONS) return L"comunicazioni";
            if (mode == AUDIO_SIGNALPROCESSINGMODE_SPEECH) return L"parlato";
            if (mode == AUDIO_SIGNALPROCESSINGMODE_MEDIA) return L"media";
            if (mode == AUDIO_SIGNALPROCESSINGMODE_MOVIE) return L"film";
            if (mode == AUDIO_SIGNALPROCESSINGMODE_NOTIFICATION) return L"notifiche";
            if (mode == GUID_NULL) return L"nessuna";
            return L"altra";
        }

        // For the log: "48000 Hz 2 can. float32", or what the type is when it is not float.
        void Describe(IAudioMediaType* type, wchar_t* text, size_t size)
        {
            UNCOMPRESSEDAUDIOFORMAT f{};
            if (!type || FAILED(type->GetUncompressedAudioFormat(&f)))
            {
                wcscpy_s(text, size, L"(nessuno)");
                return;
            }
            swprintf_s(text, size, L"%.0f Hz %u can. %s%u", f.fFramesPerSecond, f.dwSamplesPerFrame,
                       f.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT ? L"float" : L"pcm",
                       f.dwValidBitsPerSample);
        }

        bool IsFloatFormat(IAudioMediaType* type, UNCOMPRESSEDAUDIOFORMAT& format)
        {
            return type != nullptr &&
                   SUCCEEDED(type->GetUncompressedAudioFormat(&format)) &&
                   format.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT &&
                   format.dwBytesPerSampleContainer == sizeof(float) &&
                   format.dwSamplesPerFrame > 0;
        }
    }

    SbkMicFx::SbkMicFx(IUnknown* outer) : m_outer(outer ? outer : &m_inner) { ++g_objectCount; }

    SbkMicFx::~SbkMicFx() { --g_objectCount; }

    // ---- IUnknown: the interfaces delegate to the controlling unknown ------------------------

    STDMETHODIMP SbkMicFx::QueryInterface(REFIID riid, void** ppv) { return m_outer->QueryInterface(riid, ppv); }

    STDMETHODIMP_(ULONG) SbkMicFx::AddRef() { return m_outer->AddRef(); }

    STDMETHODIMP_(ULONG) SbkMicFx::Release() { return m_outer->Release(); }

    // ---- Non-delegating IUnknown: owns the reference count and the interface map -------------

    STDMETHODIMP SbkMicFx::InnerUnknown::QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv)
            return E_POINTER;

        if (riid == __uuidof(IUnknown))
        {
            *ppv = this; // the inner unknown, never the outer one
            AddRef();
            return S_OK;
        }

        IUnknown* found = nullptr;
        if (riid == __uuidof(IAudioProcessingObject))
            found = static_cast<IAudioProcessingObject*>(m_owner);
        else if (riid == __uuidof(IAudioProcessingObjectRT))
            found = static_cast<IAudioProcessingObjectRT*>(m_owner);
        else if (riid == __uuidof(IAudioProcessingObjectConfiguration))
            found = static_cast<IAudioProcessingObjectConfiguration*>(m_owner);
        else if (riid == __uuidof(IAudioSystemEffects) || riid == __uuidof(IAudioSystemEffects2))
            found = static_cast<IAudioSystemEffects2*>(m_owner);

        if (!found)
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        // AddRef through the interface itself, i.e. on the controlling unknown.
        found->AddRef();
        *ppv = found;
        return S_OK;
    }

    STDMETHODIMP_(ULONG) SbkMicFx::InnerUnknown::AddRef() { return ++m_owner->m_refs; }

    STDMETHODIMP_(ULONG) SbkMicFx::InnerUnknown::Release()
    {
        const ULONG refs = --m_owner->m_refs;
        if (refs == 0)
            delete m_owner;
        return refs;
    }

    STDMETHODIMP SbkMicFx::Reset()
    {
        m_reader.Reset();
        return S_OK;
    }

    STDMETHODIMP SbkMicFx::GetLatency(HNSTIME* pTime)
    {
        if (!pTime)
            return E_POINTER;
        *pTime = 0;
        return S_OK;
    }

    STDMETHODIMP SbkMicFx::GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps)
    {
        if (!ppRegProps)
            return E_POINTER;

        auto* props = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES)));
        if (!props)
            return E_OUTOFMEMORY;
        ZeroMemory(props, sizeof(*props));

        props->clsid = CLSID_SbkMicFx;
        props->Flags = Flags;
        wcscpy_s(props->szFriendlyName, FriendlyName);
        wcscpy_s(props->szCopyrightInfo, Copyright);
        props->u32MajorVersion = 1;
        props->u32MinorVersion = 0;
        props->u32MinInputConnections = 1;
        props->u32MaxInputConnections = 1;
        props->u32MinOutputConnections = 1;
        props->u32MaxOutputConnections = 1;
        props->u32MaxInstances = 0xFFFFFFFF;
        props->u32NumAPOInterfaces = 1;
        props->iidAPOInterfaceList[0] = __uuidof(IAudioProcessingObject);

        *ppRegProps = props;
        return S_OK;
    }

    STDMETHODIMP SbkMicFx::Initialize(UINT32 cbDataSize, BYTE* pbyData)
    {
        // Windows 8.1+ positions pass APOInitSystemEffects2: the processing mode, and whether this
        // instance only exists to be listed (sound settings) and will never process audio.
        if (pbyData && cbDataSize >= sizeof(APOInitSystemEffects2))
        {
            const auto* init = reinterpret_cast<const APOInitSystemEffects2*>(pbyData);
            m_discovery = init->InitializeForDiscoveryOnly != FALSE;
            if (!m_discovery)
                Log(L"Initialize: modalità %s", ModeName(init->AudioProcessingMode));
            return S_OK;
        }
        Log(L"Initialize, %u byte di dati", cbDataSize);
        return S_OK;
    }

    HRESULT SbkMicFx::CheckFormat(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported)
    {
        if (!requested || !supported)
            return E_POINTER;
        *supported = nullptr;

        UNCOMPRESSEDAUDIOFORMAT format{};
        if (!IsFloatFormat(requested, format))
            return APOERR_FORMAT_NOT_SUPPORTED;

        // In-place processing: input and output must match, so propose the other side's format.
        UNCOMPRESSEDAUDIOFORMAT other{};
        if (opposite && IsFloatFormat(opposite, other) &&
            (other.dwSamplesPerFrame != format.dwSamplesPerFrame || other.fFramesPerSecond != format.fFramesPerSecond))
        {
            opposite->AddRef();
            *supported = opposite;
            return S_FALSE;
        }

        requested->AddRef();
        *supported = requested;
        return S_OK;
    }

    STDMETHODIMP SbkMicFx::IsInputFormatSupported(IAudioMediaType* pOppositeFormat,
                                                  IAudioMediaType* pRequestedInputFormat,
                                                  IAudioMediaType** ppSupportedInputFormat)
    {
        return LogCheck(L"ingresso", pOppositeFormat, pRequestedInputFormat, ppSupportedInputFormat);
    }

    STDMETHODIMP SbkMicFx::IsOutputFormatSupported(IAudioMediaType* pOppositeFormat,
                                                   IAudioMediaType* pRequestedOutputFormat,
                                                   IAudioMediaType** ppSupportedOutputFormat)
    {
        return LogCheck(L"uscita", pOppositeFormat, pRequestedOutputFormat, ppSupportedOutputFormat);
    }

    HRESULT SbkMicFx::LogCheck(const wchar_t* side, IAudioMediaType* opposite, IAudioMediaType* requested,
                               IAudioMediaType** supported)
    {
        const HRESULT hr = CheckFormat(opposite, requested, supported);
        if (!m_discovery)
        {
            wchar_t asked[64], other[64];
            Describe(requested, asked, _countof(asked));
            Describe(opposite, other, _countof(other));
            Log(L"Formato %s: %s (altro lato %s) -> %s", side, asked, other,
                hr == S_OK ? L"accettato" : hr == S_FALSE ? L"proposto l'altro" : L"RIFIUTATO");
        }
        return hr;
    }

    STDMETHODIMP SbkMicFx::GetInputChannelCount(UINT32* pu32ChannelCount)
    {
        if (!pu32ChannelCount)
            return E_POINTER;
        *pu32ChannelCount = m_channels;
        return S_OK;
    }

    STDMETHODIMP SbkMicFx::LockForProcess(UINT32 u32NumInputConnections,
                                          APO_CONNECTION_DESCRIPTOR** ppInputConnections,
                                          UINT32 u32NumOutputConnections,
                                          APO_CONNECTION_DESCRIPTOR** ppOutputConnections)
    {
        if (m_locked)
            return APOERR_APO_LOCKED;
        if (u32NumInputConnections != 1 || u32NumOutputConnections != 1 ||
            !ppInputConnections || !ppOutputConnections || !ppInputConnections[0] || !ppOutputConnections[0])
            return E_INVALIDARG;

        UNCOMPRESSEDAUDIOFORMAT format{};
        if (!IsFloatFormat(ppInputConnections[0]->pFormat, format))
            return APOERR_FORMAT_NOT_SUPPORTED;

        m_channels = format.dwSamplesPerFrame;
        m_sampleRate = static_cast<UINT32>(format.fFramesPerSecond + 0.5f);
        const bool shared = m_reader.Open();
        m_locked = true;

        Log(L"LockForProcess: %u Hz, %u canali, max %u frame, memoria condivisa %s",
            m_sampleRate, m_channels, ppInputConnections[0]->u32MaxFrameCount,
            shared ? L"aperta" : L"NON disponibile");
        return S_OK;
    }

    STDMETHODIMP SbkMicFx::UnlockForProcess()
    {
        if (!m_locked)
            return APOERR_ALREADY_UNLOCKED;
        m_reader.Close();
        m_locked = false;
        Log(L"UnlockForProcess");
        return S_OK;
    }

    // Real-time thread: no locks, allocations or I/O.
    STDMETHODIMP_(void) SbkMicFx::APOProcess(UINT32 u32NumInputConnections,
                                             APO_CONNECTION_PROPERTY** ppInputConnections,
                                             UINT32 u32NumOutputConnections,
                                             APO_CONNECTION_PROPERTY** ppOutputConnections)
    {
        if (u32NumInputConnections < 1 || u32NumOutputConnections < 1)
            return;

        APO_CONNECTION_PROPERTY* input = ppInputConnections[0];
        APO_CONNECTION_PROPERTY* output = ppOutputConnections[0];
        const UINT32 frames = input->u32ValidFrameCount;
        auto* source = reinterpret_cast<float*>(input->pBuffer);
        auto* target = reinterpret_cast<float*>(output->pBuffer);
        const size_t bytes = static_cast<size_t>(frames) * m_channels * sizeof(float);

        // A silent buffer's contents are undefined, so start from real zeros before mixing.
        const bool silent = input->u32BufferFlags == BUFFER_SILENT;
        if (silent)
            ZeroMemory(target, bytes);
        else if (target != source)
            CopyMemory(target, source, bytes);

        const bool mixed = m_reader.MixInto(target, frames, m_channels, m_sampleRate);

        output->u32ValidFrameCount = frames;
        output->u32BufferFlags = (silent && !mixed) ? BUFFER_SILENT : BUFFER_VALID;
    }

    STDMETHODIMP_(UINT32) SbkMicFx::CalcInputFrames(UINT32 u32OutputFrameCount) { return u32OutputFrameCount; }

    STDMETHODIMP_(UINT32) SbkMicFx::CalcOutputFrames(UINT32 u32InputFrameCount) { return u32InputFrameCount; }

    STDMETHODIMP SbkMicFx::GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE /*Event*/)
    {
        if (!ppEffectsIds || !pcEffects)
            return E_POINTER;
        *ppEffectsIds = nullptr;
        *pcEffects = 0;
        return S_OK;
    }
}
