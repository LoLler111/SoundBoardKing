using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoundBoardKing.MicSetup;

/// <summary>Enables token privileges an administrator has but which are off by default.</summary>
internal static class Privileges
{
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";
    public const string Restore = "SeRestorePrivilege";
    public const string Backup = "SeBackupPrivilege";

    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;
    private const int ErrorNotAllAssigned = 1300;

    public static void Enable(params string[] names)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            throw new Win32Exception();
        try
        {
            foreach (var name in names)
            {
                if (!LookupPrivilegeValue(null, name, out var luid))
                    throw new Win32Exception();
                var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) ||
                    Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Privilegio {name} non disponibile.");
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state,
        int bufferLength, IntPtr previous, IntPtr returnLength);
}
