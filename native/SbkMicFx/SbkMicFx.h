#pragma once

#include <windows.h>
#include <audioenginebaseapo.h>
#include <audiomediatype.h>
#include <atomic>

#include "MicReader.h"

namespace sbk
{
    // {228F867B-1B0E-46D9-A9B4-2338E70D1F42}
    inline constexpr CLSID CLSID_SbkMicFx =
        { 0x228f867b, 0x1b0e, 0x46d9, { 0xa9, 0xb4, 0x23, 0x38, 0xe7, 0x0d, 0x1f, 0x42 } };

    inline constexpr wchar_t FriendlyName[] = L"SoundBoardKing Mic FX";
    inline constexpr wchar_t Copyright[] = L"SoundBoardKing";
    inline constexpr APO_FLAG Flags = static_cast<APO_FLAG>(
        APO_FLAG_INPLACE | APO_FLAG_SAMPLESPERFRAME_MUST_MATCH |
        APO_FLAG_FRAMESPERSECOND_MUST_MATCH | APO_FLAG_BITSPERSAMPLE_MUST_MATCH);

    // Capture-side system effect that adds SoundBoardKing's audio to the microphone signal.
    // In-place, float only, one input and one output connection.
    //
    // The audio engine creates APOs aggregated inside its own object, so this class follows
    // the COM aggregation rules: Inner() is the object's own IUnknown, and the IUnknown
    // methods of every other interface delegate to the controlling (outer) unknown.
    class SbkMicFx final :
        public IAudioProcessingObject,
        public IAudioProcessingObjectRT,
        public IAudioProcessingObjectConfiguration,
        public IAudioSystemEffects2
    {
    public:
        explicit SbkMicFx(IUnknown* outer = nullptr);
        ~SbkMicFx();

        /// The non-delegating IUnknown handed to the creator (reference count starts at 1).
        IUnknown* Inner() { return &m_inner; }

        // IUnknown (delegating)
        STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
        STDMETHODIMP_(ULONG) AddRef() override;
        STDMETHODIMP_(ULONG) Release() override;

        // IAudioProcessingObject
        STDMETHODIMP Reset() override;
        STDMETHODIMP GetLatency(HNSTIME* pTime) override;
        STDMETHODIMP GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps) override;
        STDMETHODIMP Initialize(UINT32 cbDataSize, BYTE* pbyData) override;
        STDMETHODIMP IsInputFormatSupported(IAudioMediaType* pOppositeFormat,
                                            IAudioMediaType* pRequestedInputFormat,
                                            IAudioMediaType** ppSupportedInputFormat) override;
        STDMETHODIMP IsOutputFormatSupported(IAudioMediaType* pOppositeFormat,
                                             IAudioMediaType* pRequestedOutputFormat,
                                             IAudioMediaType** ppSupportedOutputFormat) override;
        STDMETHODIMP GetInputChannelCount(UINT32* pu32ChannelCount) override;

        // IAudioProcessingObjectRT
        STDMETHODIMP_(void) APOProcess(UINT32 u32NumInputConnections,
                                       APO_CONNECTION_PROPERTY** ppInputConnections,
                                       UINT32 u32NumOutputConnections,
                                       APO_CONNECTION_PROPERTY** ppOutputConnections) override;
        STDMETHODIMP_(UINT32) CalcInputFrames(UINT32 u32OutputFrameCount) override;
        STDMETHODIMP_(UINT32) CalcOutputFrames(UINT32 u32InputFrameCount) override;

        // IAudioProcessingObjectConfiguration
        STDMETHODIMP LockForProcess(UINT32 u32NumInputConnections,
                                    APO_CONNECTION_DESCRIPTOR** ppInputConnections,
                                    UINT32 u32NumOutputConnections,
                                    APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override;
        STDMETHODIMP UnlockForProcess() override;

        // IAudioSystemEffects2
        STDMETHODIMP GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE Event) override;

    private:
        class InnerUnknown final : public IUnknown
        {
        public:
            explicit InnerUnknown(SbkMicFx* owner) : m_owner(owner) {}
            STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
            STDMETHODIMP_(ULONG) AddRef() override;
            STDMETHODIMP_(ULONG) Release() override;

        private:
            SbkMicFx* m_owner;
        };

        HRESULT CheckFormat(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported);

        InnerUnknown m_inner{ this };
        IUnknown* m_outer;  // the aggregating object, or &m_inner when standalone
        std::atomic<ULONG> m_refs{ 1 };
        bool m_locked = false;
        UINT32 m_channels = 0;
        UINT32 m_sampleRate = 0;
        MicReader m_reader;
    };

    // Module-wide object count for DllCanUnloadNow.
    extern std::atomic<long> g_objectCount;
    extern std::atomic<long> g_lockCount;
}
