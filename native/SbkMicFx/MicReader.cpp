#include "MicReader.h"

#include <sddl.h>
#include <algorithm>
#include <atomic>

namespace sbk
{
    namespace
    {
        // The producer runs as the signed-in user at medium integrity while audiodg runs as
        // LOCAL SERVICE, so the object gets an explicit DACL and a medium "no write up" label.
        constexpr wchar_t SecurityDescriptor[] =
            L"D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GRGW;;;IU)(A;;GRGW;;;AU)S:(ML;;NW;;;ME)";

        constexpr int64_t TargetLagMs = 40;     // how far behind the producer a reader starts
        constexpr int64_t MaxLagMs = 250;       // beyond this the reader jumps forward (clock drift)
        constexpr uint64_t ProducerTimeoutMs = 500;

        template <typename T>
        T LoadAcquire(T& value) { return std::atomic_ref<T>(value).load(std::memory_order_acquire); }

        template <typename T>
        void StoreRelaxed(T& value, T newValue) { std::atomic_ref<T>(value).store(newValue, std::memory_order_relaxed); }
    }

    bool MicReader::Open()
    {
        if (m_header)
            return true;

        PSECURITY_DESCRIPTOR sd = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(SecurityDescriptor, SDDL_REVISION_1, &sd, nullptr))
            return false;

        SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };
        m_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE,
                                       0, static_cast<DWORD>(SharedSize), SharedMemoryName);
        LocalFree(sd);
        if (!m_mapping)
            return false;

        void* view = MapViewOfFile(m_mapping, FILE_MAP_ALL_ACCESS, 0, 0, SharedSize);
        if (!view)
        {
            Close();
            return false;
        }

        m_header = static_cast<SharedHeader*>(view);
        m_ring = reinterpret_cast<float*>(m_header + 1);

        // A fresh mapping is zero-filled; whoever gets here first stamps it. Racing instances
        // write the same constants, so no lock is needed.
        if (LoadAcquire(m_header->magic) != SharedMagic)
        {
            m_header->version = SharedVersion;
            m_header->capacityFrames = RingFrames;
            std::atomic_ref<uint32_t>(m_header->magic).store(SharedMagic, std::memory_order_release);
        }
        else if (m_header->version != SharedVersion || m_header->capacityFrames != RingFrames)
        {
            Close();
            return false;
        }

        m_synced = false;
        return true;
    }

    void MicReader::Close()
    {
        if (m_header)
            UnmapViewOfFile(m_header);
        if (m_mapping)
            CloseHandle(m_mapping);
        m_header = nullptr;
        m_ring = nullptr;
        m_mapping = nullptr;
        m_synced = false;
    }

    bool MicReader::MixInto(float* dst, UINT32 frames, UINT32 channels, UINT32 deviceRate)
    {
        if (!m_header || channels == 0 || deviceRate == 0)
            return false;

        const uint64_t now = GetTickCount64();
        StoreRelaxed(m_header->readerHeartbeatMs, static_cast<int64_t>(now));
        StoreRelaxed(m_header->readerSampleRate, deviceRate);
        StoreRelaxed(m_header->readerChannels, channels);

        const int64_t producerBeat = LoadAcquire(m_header->producerHeartbeatMs);
        const uint32_t ringRate = LoadAcquire(m_header->ringSampleRate);
        if (LoadAcquire(m_header->producerActive) == 0 || ringRate == 0 ||
            now - static_cast<uint64_t>(producerBeat) > ProducerTimeoutMs)
        {
            m_synced = false;
            return false;
        }

        const int64_t written = LoadAcquire(m_header->writePos);
        const int64_t targetLag = ringRate * TargetLagMs / 1000;
        const int64_t maxLag = ringRate * MaxLagMs / 1000;
        if (!m_synced || written - m_pos > maxLag || m_pos > written)
        {
            m_pos = (std::max)(int64_t{ 0 }, written - targetLag);
            m_frac = 0.0;
            m_synced = true;
        }

        // Linear interpolation covers the case where the ring and the microphone run at
        // different rates; when they match, step is exactly 1 and samples are copied as-is.
        const double step = static_cast<double>(ringRate) / deviceRate;
        constexpr int64_t mask = RingFrames - 1;
        for (UINT32 i = 0; i < frames; ++i)
        {
            if (m_pos + 1 >= written)
                break; // underrun: wait for the producer instead of reading stale samples

            const float a = m_ring[m_pos & mask];
            const float b = m_ring[(m_pos + 1) & mask];
            const float sample = a + (b - a) * static_cast<float>(m_frac);

            float* frame = dst + static_cast<size_t>(i) * channels;
            for (UINT32 c = 0; c < channels; ++c)
                frame[c] = (std::clamp)(frame[c] + sample, -1.0f, 1.0f);

            m_frac += step;
            const int64_t advance = static_cast<int64_t>(m_frac);
            m_pos += advance;
            m_frac -= static_cast<double>(advance);
        }
        return true;
    }
}
