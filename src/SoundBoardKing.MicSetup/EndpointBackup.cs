using Microsoft.Win32;

namespace SoundBoardKing.MicSetup;

/// <summary>
/// Remembers everything the installer changes on one microphone, under
/// HKLM\SOFTWARE\SoundBoardKing\MicFx\Endpoints\{guid}, so uninstalling puts it back exactly.
/// Only the first original is kept: reinstalling never overwrites it with our own values.
/// </summary>
internal sealed class EndpointBackup(RegistryKey key)
{
    private const string TouchedList = "Touched";
    private const string AbsentList = "Absent";
    private const string CreatedList = "CreatedKeys";
    private const string AclKey = "Acl";
    private const string EndpointAclName = "(endpoint)";

    /// <summary>Saves the current value of <paramref name="name"/> in <paramref name="store"/> before it is changed.</summary>
    public void SaveOriginal(string store, RegistryKey source, string name)
    {
        var id = $"{store}|{name}";
        if (ReadList(TouchedList).Contains(id))
            return;

        var value = source.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
            Append(AbsentList, id);
        else
            key.SetValue(id, value, source.GetValueKind(name));
        Append(TouchedList, id);
    }

    public void Restore(string store, RegistryKey target)
    {
        var absent = ReadList(AbsentList);
        foreach (var id in ReadList(TouchedList).Where(id => id.StartsWith(store + "|", StringComparison.Ordinal)))
        {
            var name = id[(store.Length + 1)..];
            if (absent.Contains(id))
                target.DeleteValue(name, throwOnMissingValue: false);
            else if (key.GetValue(id, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value)
                target.SetValue(name, value, key.GetValueKind(id));
        }
    }

    public void SaveSecurity(string childKey, string sddl)
    {
        using var acl = key.CreateSubKey(AclKey);
        var name = childKey.Length == 0 ? EndpointAclName : childKey;
        if (acl.GetValue(name) is null)
            acl.SetValue(name, sddl);
    }

    /// <summary>Saved descriptors, children first so the endpoint key is restored last.</summary>
    public IEnumerable<(string ChildKey, string Sddl)> SavedSecurity()
    {
        using var acl = key.OpenSubKey(AclKey);
        if (acl is null)
            return [];
        return acl.GetValueNames()
            .OrderBy(name => name == EndpointAclName)
            .Select(name => (name == EndpointAclName ? "" : name, (string)acl.GetValue(name)!))
            .ToList();
    }

    public void MarkCreated(string childKey) => Append(CreatedList, childKey);

    public IReadOnlyList<string> CreatedKeys => ReadList(CreatedList);

    private List<string> ReadList(string name) => (key.GetValue(name) as string[] ?? []).ToList();

    private void Append(string name, string item)
    {
        var list = ReadList(name);
        if (list.Contains(item))
            return;
        list.Add(item);
        key.SetValue(name, list.ToArray(), RegistryValueKind.MultiString);
    }
}
