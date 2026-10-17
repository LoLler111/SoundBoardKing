// COM plumbing: class factory, DLL exports and self-registration.

#include "SbkMicFx.h"

#include <new>
#include <string>

using namespace sbk;

namespace
{
    HMODULE g_module = nullptr;

    // Lives for the whole life of the DLL, so reference counting is a no-op.
    class ClassFactory final : public IClassFactory
    {
    public:
        STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (!ppv)
                return E_POINTER;
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
            {
                *ppv = static_cast<IClassFactory*>(this);
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        STDMETHODIMP_(ULONG) AddRef() override { return 2; }
        STDMETHODIMP_(ULONG) Release() override { return 1; }

        STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
        {
            if (!ppv)
                return E_POINTER;
            *ppv = nullptr;
            if (outer)
                return CLASS_E_NOAGGREGATION;

            auto* fx = new (std::nothrow) SbkMicFx();
            if (!fx)
                return E_OUTOFMEMORY;
            const HRESULT hr = fx->QueryInterface(riid, ppv);
            fx->Release();
            return hr;
        }

        STDMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
                ++g_lockCount;
            else
                --g_lockCount;
            return S_OK;
        }
    };

    ClassFactory g_factory;

    std::wstring GuidString(REFGUID guid)
    {
        wchar_t buffer[64];
        StringFromGUID2(guid, buffer, ARRAYSIZE(buffer));
        return buffer;
    }

    std::wstring ComKey() { return L"SOFTWARE\\Classes\\CLSID\\" + GuidString(CLSID_SbkMicFx); }

    std::wstring ApoKey() { return L"SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects\\" + GuidString(CLSID_SbkMicFx); }

    LSTATUS SetString(const std::wstring& key, const wchar_t* name, const std::wstring& value)
    {
        return RegSetKeyValueW(HKEY_LOCAL_MACHINE, key.c_str(), name, REG_SZ, value.c_str(),
                               static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    }

    LSTATUS SetDword(const std::wstring& key, const wchar_t* name, DWORD value)
    {
        return RegSetKeyValueW(HKEY_LOCAL_MACHINE, key.c_str(), name, REG_DWORD, &value, sizeof(value));
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID /*reserved*/)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    if (rclsid != CLSID_SbkMicFx)
        return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 && g_lockCount == 0 ? S_OK : S_FALSE;
}

// Registers the COM class and the APO description the audio engine reads.
// Attaching the APO to a microphone is the installer's job (SoundBoardKing.MicSetup).
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, ARRAYSIZE(path)))
        return HRESULT_FROM_WIN32(GetLastError());

    const std::wstring com = ComKey();
    const std::wstring inproc = com + L"\\InprocServer32";
    const std::wstring apo = ApoKey();

    const LSTATUS results[] = {
        SetString(com, nullptr, FriendlyName),
        SetString(inproc, nullptr, path),
        SetString(inproc, L"ThreadingModel", L"Both"),
        SetString(apo, L"FriendlyName", FriendlyName),
        SetString(apo, L"Copyright", Copyright),
        SetDword(apo, L"MajorVersion", 1),
        SetDword(apo, L"MinorVersion", 0),
        SetDword(apo, L"Flags", static_cast<DWORD>(Flags)),
        SetDword(apo, L"MinInputConnections", 1),
        SetDword(apo, L"MaxInputConnections", 1),
        SetDword(apo, L"MinOutputConnections", 1),
        SetDword(apo, L"MaxOutputConnections", 1),
        SetDword(apo, L"MaxInstances", 0xFFFFFFFF),
        SetDword(apo, L"NumAPOInterfaces", 1),
        SetString(apo, L"APOInterface0", GuidString(__uuidof(IAudioProcessingObject))),
    };
    for (const LSTATUS result : results)
    {
        if (result != ERROR_SUCCESS)
            return HRESULT_FROM_WIN32(result);
    }
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, ComKey().c_str());
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, ApoKey().c_str());
    return S_OK;
}
