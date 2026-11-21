using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Accord.Core.Models;
using Accord.Core.Storage;
using Microsoft.Win32;

namespace Accord.Core.Audio;

public sealed class BootRestoreResult
{
    public int Applied { get; set; }
    public int Failed { get; set; }
    public List<string> Lines { get; } = new();
}

/// <summary>
/// Reads and modifies Windows MMDevices and SWD\MMDEVAPI audio endpoint names,
/// handles privilege elevation, SDDL backup/restore, Audiosrv restart, and mmsys.cpl update.
/// </summary>
public static class AudioRegistryService
{
    public const string KeyName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    public const string KeyDescription = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    public const string KeyAlias = "{b3f8fa53-0004-438e-9003-51a46e139bfc},26";
    public const string KeyDeviceDescProp = "{026e516e-b814-414b-83cd-856d6fef4822},2";
    public const string KeyHardwarePath = "{b3f8fa53-0004-438e-9003-51a46e139bfc},2";
    public const string KeyWaveId = "{233164c8-1b2c-4c7d-bc68-b671687a2567},1";

    private const string MmDevicesBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string SwdBase = @"SYSTEM\CurrentControlSet\Enum\SWD\MMDEVAPI";

    public static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity id = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(id);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static List<AudioDevice> LoadAllDevices()
    {
        var list = new List<AudioDevice>();
        var backups = BackupStore.Load();
        var backupKeys = new HashSet<string>(
            backups.Select(b => $"{b.Flow}|{b.Id}"),
            StringComparer.OrdinalIgnoreCase);

        string? defPlayMulti = null;
        string? defPlayComm = null;
        string? defRecMulti = null;
        string? defRecComm = null;

        try
        {
            defPlayMulti = AudioCoreCom.GetDefaultEndpoint(0, 1)?.Id;
            defPlayComm = AudioCoreCom.GetDefaultEndpoint(0, 2)?.Id;
            defRecMulti = AudioCoreCom.GetDefaultEndpoint(1, 1)?.Id;
            defRecComm = AudioCoreCom.GetDefaultEndpoint(1, 2)?.Id;
        }
        catch
        {
            // Core Audio may be temporarily restarting.
        }

        LoadFlow(list, "Render", backupKeys, defPlayMulti, defPlayComm);
        LoadFlow(list, "Capture", backupKeys, defRecMulti, defRecComm);

        list.Sort((a, b) =>
        {
            int stateOrder = StateSortOrder(a.State).CompareTo(StateSortOrder(b.State));
            if (stateOrder != 0) return stateOrder;
            int nameCmp = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            if (nameCmp != 0) return nameCmp;
            return string.Compare(a.Description, b.Description, StringComparison.CurrentCultureIgnoreCase);
        });

        return list;
    }

    private static int StateSortOrder(int state) => state switch
    {
        AudioDevice.StateActive => 0,
        AudioDevice.StateUnplugged => 1,
        AudioDevice.StateDisabled => 2,
        _ => 3
    };

    private static void LoadFlow(
        List<AudioDevice> list,
        string flow,
        HashSet<string> backupKeys,
        string? defaultMultiId,
        string? defaultCommId)
    {
        using RegistryKey? flowKey = Registry.LocalMachine.OpenSubKey($@"{MmDevicesBase}\{flow}");
        if (flowKey is null) return;

        foreach (string sub in flowKey.GetSubKeyNames())
        {
            using RegistryKey? devKey = flowKey.OpenSubKey(sub);
            if (devKey is null) continue;

            int state = devKey.GetValue("DeviceState") is int s ? s : 0;
            using RegistryKey? props = devKey.OpenSubKey("Properties");
            if (props is null) continue;

            string name = props.GetValue(KeyName) as string ?? string.Empty;
            string storedDesc = props.GetValue(KeyDescription) as string ?? string.Empty;
            string published = ReadPublishedFriendlyName(flow, sub);
            string desc = DescriptionFromPublished(name, published, storedDesc);
            string hw = props.GetValue(KeyHardwarePath) as string ?? string.Empty;
            string wave = props.GetValue(KeyWaveId) as string ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(desc)) continue;

            string endpointId = BuildEndpointId(flow, sub);
            var dev = new AudioDevice
            {
                Flow = flow,
                Id = sub,
                Name = name,
                Description = desc,
                State = state,
                HardwarePath = hw,
                WaveId = wave,
                HasBackup = backupKeys.Contains($"{flow}|{sub}"),
                IsDefaultMultimedia = string.Equals(endpointId, defaultMultiId, StringComparison.OrdinalIgnoreCase),
                IsDefaultCommunications = string.Equals(endpointId, defaultCommId, StringComparison.OrdinalIgnoreCase)
            };

            if (!dev.IsHidden && !dev.IsHandsFree)
            {
                list.Add(dev);
            }
        }
    }

    public static DeviceBackup ReadSnapshot(string flow, string id)
    {
        string propsPath = PropertiesPath(flow, id);
        using RegistryKey props = Registry.LocalMachine.OpenSubKey(propsPath)
            ?? throw new InvalidOperationException("Clé audio introuvable.");

        string name = props.GetValue(KeyName) as string ?? string.Empty;
        string storedDesc = props.GetValue(KeyDescription) as string ?? string.Empty;
        string published = ReadPublishedFriendlyName(flow, id);
        string desc = DescriptionFromPublished(name, published, storedDesc);

        object? aliasObj = props.GetValue(KeyAlias);
        object? ifaceObj = props.GetValue(KeyDeviceDescProp);
        string hw = props.GetValue(KeyHardwarePath) as string ?? string.Empty;
        string wave = props.GetValue(KeyWaveId) as string ?? string.Empty;

        var backup = new DeviceBackup
        {
            Flow = flow,
            Id = id,
            Name = name,
            Description = desc,
            HasAlias = aliasObj is string,
            Alias = aliasObj as string ?? string.Empty,
            HasInterface = ifaceObj is string,
            InterfaceName = ifaceObj as string ?? string.Empty,
            Hw = hw,
            Wave = wave
        };

        using RegistryKey? swd = Registry.LocalMachine.OpenSubKey(PublishedPath(flow, id));
        if (swd is not null)
        {
            object? friendlyObj = swd.GetValue("FriendlyName");
            object? devDescObj = swd.GetValue("DeviceDesc");
            backup.HasFriendly = friendlyObj is string;
            backup.FriendlyName = friendlyObj as string ?? string.Empty;
            backup.HasDeviceDesc = devDescObj is string;
            backup.DeviceDesc = devDescObj as string ?? string.Empty;
        }

        return backup;
    }

    public static void Rename(string flow, string id, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Le nom est vide.", nameof(name));
        }

        EnsureAdminAndPrivileges();

        string trimmedName = name.Trim();
        string trimmedDesc = description.Trim();
        string full = string.IsNullOrEmpty(trimmedDesc) ? trimmedName : $"{trimmedName} ({trimmedDesc})";

        WriteProtectedProperties(PropertiesPath(flow, id), key =>
        {
            key.SetValue(KeyName, trimmedName, RegistryValueKind.String);
            key.SetValue(KeyDescription, trimmedDesc, RegistryValueKind.String);
        });

        WritePublishedValues(flow, id, full, writeFriendly: true, full, writeDeviceDesc: true);

        RestartAudioService();

        string mmsysLabel = string.IsNullOrEmpty(trimmedDesc) ? trimmedName : trimmedDesc;
        AudioCoreCom.SetInterfaceName(BuildEndpointId(flow, id), mmsysLabel);
    }

    public static void RestoreSingle(DeviceBackup backup)
    {
        EnsureAdminAndPrivileges();

        string targetId = ResolveEndpointId(backup)
            ?? throw new InvalidOperationException("Périphérique introuvable dans le registre.");

        ApplyBackupToRegistry(backup, targetId);
        RestartAudioService();

        string label = string.IsNullOrEmpty(backup.Description) ? backup.Name : backup.Description;
        if (!string.IsNullOrWhiteSpace(label))
        {
            AudioCoreCom.SetInterfaceName(BuildEndpointId(backup.Flow, targetId), label);
        }
    }

    public static BootRestoreResult RestoreAllFromBackup(string? backupPath = null, bool testOnly = false)
    {
        var result = new BootRestoreResult();
        List<DeviceBackup> all = BackupStore.Load(backupPath);
        if (all.Count == 0)
        {
            result.Lines.Add("Aucune sauvegarde trouvée.");
            return result;
        }

        if (!testOnly)
        {
            EnsureAdminAndPrivileges();
        }

        var renderIndex = ReadHardwareIndex("Render");
        var captureIndex = ReadHardwareIndex("Capture");
        var pendingMmsys = new List<(string Flow, string Id, string Name, string Description)>();

        foreach (DeviceBackup b in all)
        {
            if (b.IsHandsFree) continue;
            try
            {
                (string Id, string How)? resolved = ResolveEndpoint(b, renderIndex, captureIndex);
                if (resolved is null)
                {
                    result.Failed++;
                    result.Lines.Add($"FAIL {b.Flow}/{b.Id} : appareil introuvable");
                    continue;
                }

                string full = string.IsNullOrEmpty(b.Description) ? b.Name : $"{b.Name} ({b.Description})";
                if (testOnly)
                {
                    result.Lines.Add($"WOULD {b.Flow} {resolved.Value.Id} via={resolved.Value.How} Friendly='{b.Name}' Interface='{b.Description}' EnumFriendly='{full}'");
                }
                else
                {
                    ApplyBackupToRegistry(b, resolved.Value.Id);
                    pendingMmsys.Add((b.Flow, resolved.Value.Id, b.Name, b.Description));
                    result.Lines.Add($"OK {b.Flow} {resolved.Value.Id} via={resolved.Value.How}");
                }
                result.Applied++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Lines.Add($"FAIL {b.Flow}/{b.Id} : {ex.Message}");
            }
        }

        if (!testOnly && result.Applied > 0)
        {
            try
            {
                RestartAudioService();
            }
            catch (Exception ex)
            {
                result.Lines.Add($"RESTART-FAIL : {ex.Message}");
            }

            foreach (var item in pendingMmsys)
            {
                try
                {
                    string label = string.IsNullOrEmpty(item.Description) ? item.Name : item.Description;
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        AudioCoreCom.SetInterfaceName(BuildEndpointId(item.Flow, item.Id), label);
                        result.Lines.Add($"MMSYS {item.Flow} {item.Id}");
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Lines.Add($"MMSYS-FAIL {item.Flow}/{item.Id} : {ex.Message}");
                }
            }
        }

        return result;
    }

    private static void ApplyBackupToRegistry(DeviceBackup b, string targetId)
    {
        string full = string.IsNullOrEmpty(b.Description) ? b.Name : $"{b.Name} ({b.Description})";
        WriteProtectedProperties(PropertiesPath(b.Flow, targetId), key =>
        {
            key.SetValue(KeyName, b.Name ?? string.Empty, RegistryValueKind.String);
            key.SetValue(KeyDescription, b.Description ?? string.Empty, RegistryValueKind.String);
            if (b.HasAlias) key.SetValue(KeyAlias, b.Alias ?? string.Empty, RegistryValueKind.String);
            if (b.HasInterface) key.SetValue(KeyDeviceDescProp, b.InterfaceName ?? string.Empty, RegistryValueKind.String);
        });

        string enumFriendly = b.HasFriendly ? (b.FriendlyName ?? string.Empty) : full;
        WritePublishedValues(b.Flow, targetId, enumFriendly, b.HasFriendly || !string.IsNullOrEmpty(full), b.DeviceDesc ?? string.Empty, b.HasDeviceDesc);
    }

    private sealed record EndpointHwEntry(string Id, string Hw, string Wave);

    private static List<EndpointHwEntry> ReadHardwareIndex(string flow)
    {
        var list = new List<EndpointHwEntry>();
        using RegistryKey? fk = Registry.LocalMachine.OpenSubKey($@"{MmDevicesBase}\{flow}");
        if (fk is null) return list;

        foreach (string sub in fk.GetSubKeyNames())
        {
            using RegistryKey? pk = fk.OpenSubKey($@"{sub}\Properties");
            if (pk is null) continue;
            string hw = pk.GetValue(KeyHardwarePath) as string ?? string.Empty;
            string wave = pk.GetValue(KeyWaveId) as string ?? string.Empty;
            list.Add(new EndpointHwEntry(sub, hw, wave));
        }

        return list;
    }

    private static string? ResolveEndpointId(DeviceBackup b)
    {
        var list = ReadHardwareIndex(b.Flow);
        var res = ResolveEndpoint(b, b.Flow == "Render" ? list : new(), b.Flow == "Capture" ? list : new());
        return res?.Id;
    }

    private static (string Id, string How)? ResolveEndpoint(
        DeviceBackup b,
        List<EndpointHwEntry> renderList,
        List<EndpointHwEntry> captureList)
    {
        using (RegistryKey? direct = Registry.LocalMachine.OpenSubKey(PropertiesPath(b.Flow, b.Id)))
        {
            if (direct is not null) return (b.Id, "direct");
        }

        var list = string.Equals(b.Flow, "Render", StringComparison.OrdinalIgnoreCase) ? renderList : captureList;
        if (!string.IsNullOrEmpty(b.Hw))
        {
            EndpointHwEntry? hit = list.FirstOrDefault(e => string.Equals(e.Hw, b.Hw, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return (hit.Id, "hw");
        }

        if (!string.IsNullOrEmpty(b.Wave))
        {
            EndpointHwEntry? hit = list.FirstOrDefault(e => string.Equals(e.Wave, b.Wave, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return (hit.Id, "wave");
        }

        return null;
    }

    public static string BuildEndpointId(string flow, string id) =>
        (string.Equals(flow, "Render", StringComparison.OrdinalIgnoreCase) ? "{0.0.0.00000000}." : "{0.0.1.00000000}.") + id;

    private static string PropertiesPath(string flow, string id) =>
        $@"{MmDevicesBase}\{flow}\{id}\Properties";

    private static string PublishedPath(string flow, string id) =>
        $@"{SwdBase}\{BuildEndpointId(flow, id)}";

    private static string ReadPublishedFriendlyName(string flow, string id)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PublishedPath(flow, id));
        return key?.GetValue("FriendlyName") as string ?? string.Empty;
    }

    private static string DescriptionFromPublished(string name, string published, string stored)
    {
        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(published))
        {
            string prefix = name + " (";
            if (published.StartsWith(prefix, StringComparison.Ordinal) && published.EndsWith(")", StringComparison.Ordinal))
            {
                return published.Substring(prefix.Length, published.Length - prefix.Length - 1);
            }
        }
        return stored;
    }

    private static void WritePublishedValues(string flow, string id, string friendly, bool writeFriendly, string devDesc, bool writeDeviceDesc)
    {
        string path = PublishedPath(flow, id);
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (key is null) return;
            if (writeFriendly) key.SetValue("FriendlyName", friendly, RegistryValueKind.String);
            if (writeDeviceDesc) key.SetValue("DeviceDesc", devDesc, RegistryValueKind.String);
        }
        catch (UnauthorizedAccessException)
        {
            WriteProtectedProperties(path, key =>
            {
                if (writeFriendly) key.SetValue("FriendlyName", friendly, RegistryValueKind.String);
                if (writeDeviceDesc) key.SetValue("DeviceDesc", devDesc, RegistryValueKind.String);
            });
        }
    }

    private static void WriteProtectedProperties(string subKeyPath, Action<RegistryKey> writeAction)
    {
        const AccessControlSections AllSections =
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        string originalSddl;
        using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            subKeyPath,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.TakeOwnership | RegistryRights.ReadPermissions))
        {
            if (key is null) throw new InvalidOperationException("Clé registre introuvable : " + subKeyPath);
            RegistrySecurity sec = key.GetAccessControl(AllSections);
            originalSddl = sec.GetSecurityDescriptorSddlForm(AllSections);
            sec.SetOwner(adminSid);
            key.SetAccessControl(sec);
        }

        try
        {
            using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                subKeyPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions))
            {
                if (key is null) throw new InvalidOperationException("Impossible de modifier les permissions : " + subKeyPath);
                RegistrySecurity sec = key.GetAccessControl(AccessControlSections.Access);
                var rule = new RegistryAccessRule(
                    adminSid,
                    RegistryRights.FullControl,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow);
                sec.AddAccessRule(rule);
                key.SetAccessControl(sec);
            }

            using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true))
            {
                if (key is null) throw new InvalidOperationException("Impossible d'écrire dans la clé : " + subKeyPath);
                writeAction(key);
            }
        }
        finally
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                subKeyPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions | RegistryRights.TakeOwnership);
            if (key is not null)
            {
                RegistrySecurity restore = key.GetAccessControl(AllSections);
                restore.SetSecurityDescriptorSddlForm(originalSddl, AllSections);
                key.SetAccessControl(restore);
            }
        }
    }

    private static void EnsureAdminAndPrivileges()
    {
        if (!IsCurrentProcessElevated())
        {
            throw new UnauthorizedAccessException("Droits administrateur requis.");
        }

        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        EnablePrivilege("SeBackupPrivilege");
    }

    private static void EnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0028 /* TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY */, out IntPtr token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = 0x00000002 // SE_PRIVILEGE_ENABLED
            };

            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            int err = Marshal.GetLastWin32Error();
            if (err != 0)
            {
                throw new Win32Exception(err);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Restarts Audiosrv (and any active non-AarSvc dependent services) using native Win32 SCM APIs.
    /// </summary>
    public static void RestartAudioService()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            IntPtr audioSvc = OpenServiceW(scm, "Audiosrv", SERVICE_STOP | SERVICE_START | SERVICE_QUERY_STATUS | SERVICE_ENUMERATE_DEPENDENTS);
            if (audioSvc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            var stoppedDeps = new List<string>();
            try
            {
                List<string> dependents = EnumActiveDependentServices(audioSvc);
                foreach (string depName in dependents)
                {
                    if (depName.StartsWith("AarSvc", StringComparison.OrdinalIgnoreCase)) continue;
                    IntPtr depHandle = OpenServiceW(scm, depName, SERVICE_STOP | SERVICE_START | SERVICE_QUERY_STATUS);
                    if (depHandle == IntPtr.Zero) continue;
                    try
                    {
                        if (StopAndWait(depHandle, TimeSpan.FromSeconds(20)))
                        {
                            stoppedDeps.Add(depName);
                        }
                    }
                    finally
                    {
                        CloseServiceHandle(depHandle);
                    }
                }

                StopAndWait(audioSvc, TimeSpan.FromSeconds(20));
                StartAndWait(audioSvc, TimeSpan.FromSeconds(20));

                foreach (string depName in stoppedDeps)
                {
                    IntPtr depHandle = OpenServiceW(scm, depName, SERVICE_START | SERVICE_QUERY_STATUS);
                    if (depHandle == IntPtr.Zero) continue;
                    try
                    {
                        StartAndWait(depHandle, TimeSpan.FromSeconds(15));
                    }
                    catch
                    {
                        // Best-effort restart of dependent services.
                    }
                    finally
                    {
                        CloseServiceHandle(depHandle);
                    }
                }
            }
            finally
            {
                CloseServiceHandle(audioSvc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static List<string> EnumActiveDependentServices(IntPtr service)
    {
        var names = new List<string>();
        EnumDependentServicesW(service, SERVICE_ACTIVE, IntPtr.Zero, 0, out uint bytesNeeded, out _);
        if (bytesNeeded == 0) return names;

        IntPtr buf = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            if (!EnumDependentServicesW(service, SERVICE_ACTIVE, buf, bytesNeeded, out _, out uint count))
            {
                return names;
            }

            int structSize = Marshal.SizeOf<ENUM_SERVICE_STATUS>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<ENUM_SERVICE_STATUS>(IntPtr.Add(buf, i * structSize));
                string? name = Marshal.PtrToStringUni(item.lpServiceName);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return names;
    }

    private static bool StopAndWait(IntPtr service, TimeSpan timeout)
    {
        if (!QueryServiceStatus(service, out SERVICE_STATUS status)) return false;
        if (status.dwCurrentState == SERVICE_STOPPED) return false;

        ControlService(service, SERVICE_CONTROL_STOP, out _);
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (QueryServiceStatus(service, out status) && status.dwCurrentState == SERVICE_STOPPED)
            {
                return true;
            }
            Thread.Sleep(120);
        }
        return true;
    }

    private static void StartAndWait(IntPtr service, TimeSpan timeout)
    {
        if (QueryServiceStatus(service, out SERVICE_STATUS status) && status.dwCurrentState == SERVICE_RUNNING)
        {
            return;
        }

        StartServiceW(service, 0, IntPtr.Zero);
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (QueryServiceStatus(service, out status) && status.dwCurrentState == SERVICE_RUNNING)
            {
                return;
            }
            Thread.Sleep(120);
        }
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_ACTIVE = 0x0001;
    private const uint SERVICE_CONTROL_STOP = 0x0001;
    private const uint SERVICE_STOPPED = 0x0001;
    private const uint SERVICE_RUNNING = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ENUM_SERVICE_STATUS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS ServiceStatus;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceW(IntPtr service, uint numServiceArgs, IntPtr serviceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumDependentServicesW(
        IntPtr service,
        uint serviceState,
        IntPtr services,
        uint bufSize,
        out uint bytesNeeded,
        out uint servicesReturned);
}
