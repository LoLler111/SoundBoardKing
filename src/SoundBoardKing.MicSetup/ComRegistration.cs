using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoundBoardKing.MicSetup;

/// <summary>Calls DllRegisterServer / DllUnregisterServer exported by the APO, like regsvr32.</summary>
internal static class ComRegistration
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RegistrationFunction();

    public static void Register(string dllPath) => Invoke(dllPath, "DllRegisterServer");

    public static void Unregister(string dllPath) => Invoke(dllPath, "DllUnregisterServer");

    private static void Invoke(string dllPath, string export)
    {
        var module = LoadLibraryExW(dllPath, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Impossibile caricare {dllPath}.");
        try
        {
            var function = GetProcAddress(module, export);
            if (function == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"{export} non trovata in {dllPath}.");
            int hr = Marshal.GetDelegateForFunctionPointer<RegistrationFunction>(function)();
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private const uint LoadWithAlteredSearchPath = 0x8;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
}
