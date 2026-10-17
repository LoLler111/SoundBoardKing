#include "SbkMicFx.h"

#include <initguid.h>
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>

#include "Log.h"

namespace sbk
{
    std::atomic<long> g_objectCount{ 0 };
    std::atomic<long> g_lockCount{ 0 };

    namespace
    {
        bool IsFloatFormat(IAudioMediaType* type, UNCOMPRESSEDAUDIOFORMAT& format)
        {
            return type != nullptr &&
                   SUCCEEDED(type->GetUncompressedAudioFormat(&format)) &&
                   format.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT &&
                   format.dwBytesPerSampleContainer == sizeof(float) &&
                   format.dwSamplesPerFrame > 0;
        }
    }

    SbkMicFx::SbkMicFx() { ++g_objectCount; }

    SbkMicFx::~SbkMicFx() { --g_objectCount; }

    STDMETHODIMP SbkMicFx::QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv)
            return E_POINTER;

        if (riid == __uuidof(IUnknown) || riid == __uuidof(IAudioProcessingObject))
            *ppv = static_cast<IAudioProcessingObject*>(this);
        else if (riid == __uuidof(IAudioProcessingObjectRT))
            *ppv = static_cast<IAudioProcessingObjectRT*>(this);
        else if (riid == __uuidof(IAudioProcessingObjectConfiguration))
            *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (riid == __uuidof(IAudioSystemEffects) || riid == __uuidof(IAudioSystemEffects2))
            *ppv = static_cast<IAudioSystemEffects2*>(this);
        else
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    STDMETHODIMP_(ULONG) SbkMicFx::AddRef() { return ++m_refs; }

    STDMETHODIMP_(ULONG) SbkMicFx::Release()
    {
        const ULONG refs = --m_refs;
        if (refs == 0)
            delete this;
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

    STDMETHODIMP SbkMicFx::Initialize(UINT32 cbDataSize, BYTE* /*pbyData*/)
    {
        // The init blob (APOInitSystemEffects2/3) carries endpoint details this APO does not need.
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
        return CheckFormat(pOppositeFormat, pRequestedInputFormat, ppSupportedInputFormat);
    }

    STDMETHODIMP SbkMicFx::IsOutputFormatSupported(IAudioMediaType* pOppositeFormat,
                                                   IAudioMediaType* pRequestedOutputFormat,
                                                   IAudioMediaType** ppSupportedOutputFormat)
    {
        return CheckFormat(pOppositeFormat, pRequestedOutputFormat, ppSupportedOutputFormat);
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
