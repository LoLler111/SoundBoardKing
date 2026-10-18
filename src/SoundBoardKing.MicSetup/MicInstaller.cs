using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace SoundBoardKing.MicSetup;

/// <summary>Where in the capture chain the APO is attached. Values are the FX property ids.</summary>
public enum FxSlot
{
    /// <summary>Picks <see cref="Sfx"/> or <see cref="Lfx"/> from the effects the driver already declares.</summary>
    Auto = 0,

    /// <summary>Windows 7-style local (pre-mix) effect, used by drivers without signal processing modes.</summary>
    Lfx = 1,

    /// <summary>Windows 7-style global (post-mix) effect.</summary>
    Gfx = 2,

    /// <summary>Per-stream effect: one instance per application capturing.</summary>
    Sfx = 5,

    /// <summary>Per-mode effect (default, communications…).</summary>
    Mfx = 6,

    /// <summary>Endpoint effect: one instance for the microphone, also applied to raw streams.</summary>
    Efx = 7,
}

internal sealed class MicInstaller(Action<string> log)
{
    public const string Clsid = "{228F867B-1B0E-46D9-A9B4-2338E70D1F42}";
    public const string StateKeyPath = @"SOFTWARE\SoundBoardKing\MicFx";

    private const string CaptureRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
    private const string AudioPolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio";
    private const string FxPropertyFormat = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
    private const string ModesPropertyFormat = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}";
    private const string DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
    private const string DisableSysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    private const string FxStore = "FxProperties";
    private const string PropertiesStore = "Properties";
    private const string DllName = "SbkMicFx.dll";

    private static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SoundBoardKing", "MicFx");

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SoundBoardKing");

    /// <summary>"{0.0.1.00000000}.{guid}" → "{guid}"; only capture devices are accepted.</summary>
    public static string EndpointGuid(string deviceId)
    {
        if (!deviceId.StartsWith("{0.0.1.", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"\"{deviceId}\" non è un dispositivo di registrazione.");
        var guid = deviceId[(deviceId.LastIndexOf('.') + 1)..];
        if (!Guid.TryParse(guid, out _))
            throw new ArgumentException($"Id dispositivo non valido: {deviceId}");
        return guid;
    }

    public void Install(string deviceId, FxSlot slot, string sourceDll)
    {
        var guid = EndpointGuid(deviceId);
        var endpointPath = $@"{CaptureRoot}\{guid}";
        using (var probe = Registry.LocalMachine.OpenSubKey(endpointPath))
        {
            if (probe is null)
                throw new InvalidOperationException($"Microfono non trovato nel registro: {deviceId}");
        }

        PrepareDataDirectory();
        var dll = CopyDll(sourceDll);
        ComRegistration.Register(dll);
        log($"APO registrata: {dll}");

        using var state = Registry.LocalMachine.CreateSubKey(StateKeyPath);
        state.SetValue("InstallPath", dll);
        DisableProtectedAudioDg(state);

        using var backupKey = state.CreateSubKey($@"Endpoints\{guid}");
        var backup = new EndpointBackup(backupKey);
        backupKey.SetValue("DeviceId", deviceId);

        // Make the endpoint writable first: creating FxProperties needs it.
        RegistryAccess.OpenWritable(endpointPath, sddl => backup.SaveSecurity("", sddl)).Dispose();

        using (var fx = OpenOrCreateChild(endpointPath, FxStore, backup))
        {
            // Reinstalling (e.g. with another slot) starts again from the original values.
            backup.Restore(FxStore, fx);
            if (slot == FxSlot.Auto)
            {
                slot = ChooseSlot(fx);
                log($"Posizione scelta automaticamente: {slot}.");
            }
            backupKey.SetValue("Slot", slot.ToString());
            ApplySlot(fx, slot, backup);
        }

        using (var properties = OpenOrCreateChild(endpointPath, PropertiesStore, backup))
        {
            backup.SaveOriginal(PropertiesStore, properties, DisableSysFx);
            properties.SetValue(DisableSysFx, 0, RegistryValueKind.DWord);
            log("Miglioramenti audio attivati sul microfono.");
        }

        AudioService.Restart(log);
        log($"Installazione completata su {deviceId} ({slot}).");
    }

    public void Uninstall(string? deviceId)
    {
        using var state = Registry.LocalMachine.OpenSubKey(StateKeyPath, writable: true);
        using var endpoints = state?.OpenSubKey("Endpoints", writable: true);
        if (state is null || endpoints is null)
        {
            log("SoundBoardKing non è installato su nessun microfono.");
            return;
        }

        var guids = deviceId is null ? endpoints.GetSubKeyNames() : [EndpointGuid(deviceId)];
        foreach (var guid in guids)
            RestoreEndpoint(endpoints, guid);

        if (endpoints.SubKeyCount > 0)
        {
            AudioService.Restart(log);
            return;
        }

        // Last microphone gone: remove the APO itself.
        RestoreProtectedAudioDg(state);
        var dll = state.GetValue("InstallPath") as string;
        AudioService.Stop(log);
        if (dll is not null && File.Exists(dll))
        {
            ComRegistration.Unregister(dll);
            File.Delete(dll);
            log($"APO rimossa: {dll}");
        }
        endpoints.Dispose();
        state.Dispose();
        Registry.LocalMachine.DeleteSubKeyTree(StateKeyPath, throwOnMissingSubKey: false);
        using (var company = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SoundBoardKing"))
        {
            if (company is { SubKeyCount: 0, ValueCount: 0 })
                Registry.LocalMachine.DeleteSubKey(@"SOFTWARE\SoundBoardKing", throwOnMissingSubKey: false);
        }
        AudioService.Start(log);
        log("Disinstallazione completata.");
    }

    public IEnumerable<string> InstalledDevices()
    {
        using var endpoints = Registry.LocalMachine.OpenSubKey($@"{StateKeyPath}\Endpoints");
        if (endpoints is null)
            yield break;
        foreach (var guid in endpoints.GetSubKeyNames())
        {
            using var endpoint = endpoints.OpenSubKey(guid);
            yield return $"{endpoint?.GetValue("DeviceId")} ({endpoint?.GetValue("Slot")})";
        }
    }

    // Windows reads either the Windows 8.1+ positions (SFX/MFX/EFX) or the Windows 7 ones
    // (LFX/GFX), depending on the driver. A driver that already declares modern effects gets SFX;
    // one with none or only legacy effects (seen with VB-CABLE: only LFX/GFX load) gets LFX.
    private static FxSlot ChooseSlot(RegistryKey fx)
    {
        var names = fx.GetValueNames();
        bool modern = new[] { 5, 6, 7, 13, 14, 15 }
            .Any(id => names.Contains($"{FxPropertyFormat},{id}", StringComparer.OrdinalIgnoreCase));
        return modern ? FxSlot.Sfx : FxSlot.Lfx;
    }

    // With a free slot the APO goes straight in. When the driver already put its own effect
    // there, both are listed in the composite property so the driver's effect keeps working.
    private void ApplySlot(RegistryKey fx, FxSlot slot, EndpointBackup backup)
    {
        var single = $"{FxPropertyFormat},{(int)slot}";
        backup.SaveOriginal(FxStore, fx, single);
        var current = fx.GetValue(single) as string;

        // The Windows 7-style positions have no composite list: the driver's effect, if any,
        // is replaced until uninstall puts it back.
        if (slot is FxSlot.Lfx or FxSlot.Gfx)
        {
            fx.SetValue(single, Clsid, RegistryValueKind.String);
            log(string.IsNullOrEmpty(current) || string.Equals(current, Clsid, StringComparison.OrdinalIgnoreCase)
                ? $"Impostata in {single}."
                : $"Impostata in {single} al posto di {current} (verrà ripristinato alla rimozione).");
            return;
        }

        EnsureDefaultMode(fx, slot, backup);
        var composite = $"{FxPropertyFormat},{(int)slot + 8}";
        backup.SaveOriginal(FxStore, fx, composite);
        var list = fx.GetValue(composite) as string[];
        if (list is not null)
        {
            if (!list.Contains(Clsid, StringComparer.OrdinalIgnoreCase))
                fx.SetValue(composite, list.Append(Clsid).ToArray(), RegistryValueKind.MultiString);
            log($"Aggiunta alla lista composita {composite} ({list.Length} effetti del driver mantenuti).");
        }
        else if (string.IsNullOrEmpty(current) || string.Equals(current, Clsid, StringComparison.OrdinalIgnoreCase))
        {
            fx.SetValue(single, Clsid, RegistryValueKind.String);
            log($"Impostata in {single}.");
        }
        else
        {
            fx.SetValue(composite, new[] { current, Clsid }, RegistryValueKind.MultiString);
            log($"Lo slot {single} era occupato da {current}: create la lista composita {composite} con entrambi.");
        }
    }

    // The Windows 8.1+ positions only run in the processing modes listed next to them: an effect
    // without that list is ignored (seen with a SteelSeries headset declaring only its own EFX).
    // Without one from the driver, declare the default mode, which ordinary recording uses.
    private void EnsureDefaultMode(RegistryKey fx, FxSlot slot, EndpointBackup backup)
    {
        var modes = $"{ModesPropertyFormat},{(int)slot}";
        backup.SaveOriginal(FxStore, fx, modes);
        if (fx.GetValue(modes) is string[] { Length: > 0 } list)
        {
            log($"Modalità già dichiarate dal driver per {slot}: {string.Join(", ", list)}.");
            return;
        }
        fx.SetValue(modes, new[] { DefaultMode }, RegistryValueKind.MultiString);
        log($"Dichiarata la modalità predefinita per {slot} ({modes}).");
    }

    private void RestoreEndpoint(RegistryKey endpoints, string guid)
    {
        using (var backupKey = endpoints.OpenSubKey(guid, writable: true))
        {
            if (backupKey is null)
            {
                log($"Nessun backup per il microfono {guid}.");
                return;
            }

            var backup = new EndpointBackup(backupKey);
            var endpointPath = $@"{CaptureRoot}\{guid}";
            foreach (var store in new[] { FxStore, PropertiesStore })
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{endpointPath}\{store}", writable: true);
                if (key is not null)
                    backup.Restore(store, key);
            }

            using (var endpoint = Registry.LocalMachine.OpenSubKey(endpointPath, writable: true))
            {
                foreach (var created in backup.CreatedKeys)
                {
                    using var child = endpoint?.OpenSubKey(created);
                    if (child is { ValueCount: 0, SubKeyCount: 0 })
                        endpoint!.DeleteSubKey(created, throwOnMissingSubKey: false);
                }
            }

            foreach (var (child, sddl) in backup.SavedSecurity())
                RegistryAccess.RestoreSecurity(child.Length == 0 ? endpointPath : $@"{endpointPath}\{child}", sddl);
        }

        endpoints.DeleteSubKeyTree(guid);
        log($"Microfono {guid} ripristinato.");
    }

    private RegistryKey OpenOrCreateChild(string endpointPath, string child, EndpointBackup backup)
    {
        var path = $@"{endpointPath}\{child}";
        using (var existing = Registry.LocalMachine.OpenSubKey(path))
        {
            if (existing is not null)
                return RegistryAccess.OpenWritable(path, sddl => backup.SaveSecurity(child, sddl));
        }

        using var endpoint = Registry.LocalMachine.OpenSubKey(endpointPath, writable: true)!;
        backup.MarkCreated(child);
        return endpoint.CreateSubKey(child);
    }

    private void DisableProtectedAudioDg(RegistryKey state)
    {
        using var audio = Registry.LocalMachine.CreateSubKey(AudioPolicyKey);
        if (state.GetValue("ProtectedAudioDGBackup") is null)
            state.SetValue("ProtectedAudioDGBackup", audio.GetValue("DisableProtectedAudioDG") is int value ? value.ToString() : "absent");
        audio.SetValue("DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
        log("DisableProtectedAudioDG = 1");
    }

    private void RestoreProtectedAudioDg(RegistryKey state)
    {
        using var audio = Registry.LocalMachine.CreateSubKey(AudioPolicyKey);
        var saved = state.GetValue("ProtectedAudioDGBackup") as string;
        if (saved is null or "absent")
            audio.DeleteValue("DisableProtectedAudioDG", throwOnMissingValue: false);
        else
            audio.SetValue("DisableProtectedAudioDG", int.Parse(saved), RegistryValueKind.DWord);
        log($"DisableProtectedAudioDG ripristinato ({saved ?? "absent"}).");
    }

    private string CopyDll(string source)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException($"{DllName} non trovata accanto all'installer.", source);
        Directory.CreateDirectory(InstallDirectory);
        var target = Path.Combine(InstallDirectory, DllName);
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source)))
            return target;

        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (IOException)
        {
            // The old copy is loaded by audiodg.exe; stopping the audio service releases it.
            AudioService.Stop(log);
            File.Copy(source, target, overwrite: true);
        }
        log($"Copiata {DllName} in {InstallDirectory}");
        return target;
    }

    // The APO writes its log here while running as LOCAL SERVICE.
    private static void PrepareDataDirectory()
    {
        var directory = Directory.CreateDirectory(DataDirectory);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}
