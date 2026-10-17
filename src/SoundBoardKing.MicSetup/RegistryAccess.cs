using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace SoundBoardKing.MicSetup;

/// <summary>
/// The microphone's registry keys belong to the audio service and are read-only even for
/// administrators. These helpers take ownership when needed and can put the original
/// security descriptor back.
/// </summary>
internal static class RegistryAccess
{
    private const AccessControlSections OwnerAndAccess = AccessControlSections.Owner | AccessControlSections.Access;
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// Opens HKLM\<paramref name="path"/> for writing. When the ACL has to be changed first,
    /// the original descriptor (SDDL) is handed to <paramref name="saveOriginal"/>.
    /// </summary>
    public static RegistryKey OpenWritable(string path, Action<string> saveOriginal)
    {
        try
        {
            return Registry.LocalMachine.OpenSubKey(path, writable: true)
                ?? throw new InvalidOperationException($"Chiave non trovata: HKLM\\{path}");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
        }

        Privileges.Enable(Privileges.TakeOwnership, Privileges.Restore);

        using (var key = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryRights.TakeOwnership | RegistryRights.ReadPermissions)
               ?? throw new InvalidOperationException($"Chiave non trovata: HKLM\\{path}"))
        {
            var security = key.GetAccessControl(OwnerAndAccess);
            saveOriginal(security.GetSecurityDescriptorSddlForm(OwnerAndAccess));
            security.SetOwner(Administrators);
            key.SetAccessControl(security);
        }

        using (var key = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!)
        {
            var security = key.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new RegistryAccessRule(Administrators, RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            key.SetAccessControl(security);
        }

        return Registry.LocalMachine.OpenSubKey(path, writable: true)!;
    }

    public static void RestoreSecurity(string path, string sddl)
    {
        Privileges.Enable(Privileges.TakeOwnership, Privileges.Restore);
        using var key = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.TakeOwnership | RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
        if (key is null)
            return;
        var security = new RegistrySecurity();
        security.SetSecurityDescriptorSddlForm(sddl, OwnerAndAccess);
        key.SetAccessControl(security);
    }
}
