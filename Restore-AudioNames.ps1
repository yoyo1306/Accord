# Restaure la sauvegarde AudioRename (AudioRename.backup.txt) apres un boot.
# (pas de #Requires : le mode -TestOnly doit tourner sans admin)
# Utilisé par la case "Restaurer la dernière sauvegarde au démarrage" (tâche planifiée).
#  - Lit Flow/Id/Name/Description (+Alias/Interface/Friendly/DeviceDesc, Hw/Wave) du backup
#  - Résout chaque appareil : GUID direct, sinon chemin matériel (Hw), sinon Wave
#  - Réécrit les valeurs (avec prise de propriété comme l'app), redémarre l'audio une fois
#  - Puis repose le nom du panneau Son (mmsys.cpl) : le redémarrage audio réécrit sinon le nom matériel
#  - Journal : AudioRename.autorestore.log
# Mode test (lecture seule, sans admin) : -TestOnly [-Dir ...]
# Un seul appareil, sans redémarrer l'audio : -MmsysOnly -Flow Render -Id {guid} -Name X -Description Y
param([switch]$TestOnly, [string]$Dir = $PSScriptRoot, [switch]$MmsysOnly, [string]$Flow, [string]$Id, [string]$Name, [string]$Description)

$ErrorActionPreference = 'Stop'
$FRIENDLY = '{a45c254e-df1c-4efd-8020-67d146a850e0},2'
$IFACE    = '{b3f8fa53-0004-438e-9003-51a46e139bfc},6'
$ALIAS    = '{b3f8fa53-0004-438e-9003-51a46e139bfc},26'
$DEVDESC  = '{026e516e-b814-414b-83cd-856d6fef4822},2'
$HWPATH   = '{b3f8fa53-0004-438e-9003-51a46e139bfc},2'
$WAVE     = '{233164c8-1b2c-4c7d-bc68-b671687a2567},1'

function Unescape-Backup($s) {
    $sb = New-Object System.Text.StringBuilder
    $i = 0
    while ($i -lt $s.Length) {
        $c = $s[$i]
        if ($c -eq '\' -and ($i + 1) -lt $s.Length) {
            $n = $s[$i + 1]
            if ($n -eq '\') { $sb.Append('\') | Out-Null; $i += 2; continue }
            if ($n -eq 'r') { $sb.Append("`r") | Out-Null; $i += 2; continue }
            if ($n -eq 'n') { $sb.Append("`n") | Out-Null; $i += 2; continue }
        }
        $sb.Append($c) | Out-Null; $i++
    }
    return $sb.ToString()
}

function Load-Backup($path) {
    $list = @()
    if (-not (Test-Path $path)) { return $list }
    $cur = $null
    foreach ($line in [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)) {
        if ($line -eq '---') {
            if ($cur -ne $null -and $cur.Id -ne '') { $list += $cur }
            $cur = $null
            continue
        }
        $eq = $line.IndexOf('=')
        if ($eq -lt 0) { continue }
        if ($cur -eq $null) {
            $cur = New-Object PSObject -Property @{
                Flow = ''; Id = ''; Name = ''; Description = ''
                HasAlias = $false; Alias = ''
                HasInterface = $false; InterfaceName = ''
                HasFriendly = $false; FriendlyName = ''
                HasDeviceDesc = $false; DeviceDesc = ''
                Hw = ''; Wave = ''
            }
        }
        $key = $line.Substring(0, $eq)
        $val = Unescape-Backup $line.Substring($eq + 1)
        switch ($key) {
            'Flow' { $cur.Flow = $val }
            'Id' { $cur.Id = $val }
            'Name' { $cur.Name = $val }
            'Description' { $cur.Description = $val }
            'HasAlias' { $cur.HasAlias = ($val -eq '1') }
            'Alias' { $cur.Alias = $val }
            'HasInterface' { $cur.HasInterface = ($val -eq '1') }
            'Interface' { $cur.InterfaceName = $val }
            'HasFriendly' { $cur.HasFriendly = ($val -eq '1') }
            'FriendlyName' { $cur.FriendlyName = $val }
            'HasDeviceDesc' { $cur.HasDeviceDesc = ($val -eq '1') }
            'DeviceDesc' { $cur.DeviceDesc = $val }
            'Hw' { $cur.Hw = $val }
            'Wave' { $cur.Wave = $val }
        }
    }
    return $list
}

function Get-Endpoints($flow) {
    $out = @()
    $base = "SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\$flow"
    $fk = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($base)
    if ($fk -eq $null) { return $out }
    try {
        foreach ($sub in $fk.GetSubKeyNames()) {
            $pk = $fk.OpenSubKey("$sub\Properties")
            if ($pk -eq $null) { continue }
            try {
                $hw = $pk.GetValue($HWPATH)
                $wv = $pk.GetValue($WAVE)
                $out += New-Object PSObject -Property @{
                    Id = $sub
                    Hw = if ($hw -is [string]) { $hw } else { '' }
                    Wave = if ($wv -is [string]) { $wv } else { '' }
                }
            } finally { $pk.Dispose() }
        }
    } finally { $fk.Dispose() }
    return $out
}

function Resolve-Endpoint($b, $renderList, $captureList) {
    $props = "SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\$($b.Flow)\$($b.Id)\Properties"
    $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($props)
    if ($k -ne $null) { $k.Dispose(); return @{ Id = $b.Id; How = 'direct' } }
    $list = if ($b.Flow -eq 'Render') { $renderList } else { $captureList }
    if ($b.Hw -ne '') {
        foreach ($e in $list) { if ($e.Hw -eq $b.Hw) { return @{ Id = $e.Id; How = 'hw' } } }
    }
    if ($b.Wave -ne '') {
        foreach ($e in $list) { if ($e.Wave -eq $b.Wave) { return @{ Id = $e.Id; How = 'wave' } } }
    }
    return $null
}

function Enable-Privileges {
    $code = @'
using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
public static class PrivilegeHelper {
  [StructLayout(LayoutKind.Sequential)] public struct Luid { public uint LowPart; public int HighPart; }
  [StructLayout(LayoutKind.Sequential)] public struct TokPriv { public uint Count; public Luid Luid; public uint Attr; }
  [DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(IntPtr h, int acc, out IntPtr t);
  [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern bool LookupPrivilegeValue(string s, string n, out Luid l);
  [DllImport("advapi32.dll", SetLastError=true)] public static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TokPriv p, int l, IntPtr pr, IntPtr r);
  public static void Enable(string n) {
    IntPtr h; if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 40, out h)) throw new Win32Exception(Marshal.GetLastWin32Error());
    Luid l; if (!LookupPrivilegeValue(null, n, out l)) throw new Win32Exception(Marshal.GetLastWin32Error());
    TokPriv tp = new TokPriv(); tp.Count=1; tp.Luid=l; tp.Attr=2;
    if (!AdjustTokenPrivileges(h,false,ref tp,0,IntPtr.Zero,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    int e = Marshal.GetLastWin32Error(); if (e!=0) throw new Win32Exception(e);
  }
}
'@
    Add-Type -TypeDefinition $code -ErrorAction Stop
    [PrivilegeHelper]::Enable('SeTakeOwnershipPrivilege')
    [PrivilegeHelper]::Enable('SeRestorePrivilege')
    [PrivilegeHelper]::Enable('SeBackupPrivilege')
}

function Write-MMDevice($flow, $id, $values) {
    $path = "SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\$flow\$id\Properties"
    $lm = [Microsoft.Win32.Registry]::LocalMachine
    $k = $lm.OpenSubKey($path, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, ([System.Security.AccessControl.RegistryRights]655360))
    if ($k -eq $null) { throw 'Clé audio introuvable.' }
    try {
        $sec = $k.GetAccessControl([System.Security.AccessControl.AccessControlSections]14)
        $sddl = $sec.GetSecurityDescriptorSddlForm([System.Security.AccessControl.AccessControlSections]14)
        $admin = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
        $sec.SetOwner($admin)
        $k.SetAccessControl($sec)
    } finally { $k.Dispose() }
    $k = $lm.OpenSubKey($path, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, ([System.Security.AccessControl.RegistryRights]393216))
    try {
        $sec = $k.GetAccessControl([System.Security.AccessControl.AccessControlSections]2)
        $admin = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
        $rule = New-Object System.Security.AccessControl.RegistryAccessRule($admin, ([System.Security.AccessControl.RegistryRights]983103), 'None', 'None', 'Allow')
        $sec.AddAccessRule($rule)
        $k.SetAccessControl($sec)
    } finally { $k.Dispose() }
    $k = $lm.OpenSubKey($path, $true)
    try {
        foreach ($n in $values.Keys) { $k.SetValue($n, $values[$n], 'String') }
    } finally { $k.Dispose() }
    $k = $lm.OpenSubKey($path, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, ([System.Security.AccessControl.RegistryRights]393216))
    try {
        $sec = $k.GetAccessControl([System.Security.AccessControl.AccessControlSections]14)
        $sec.SetSecurityDescriptorSddlForm($sddl, [System.Security.AccessControl.AccessControlSections]14)
        $k.SetAccessControl($sec)
    } finally { $k.Dispose() }
}

function Write-EnumValues($flow, $id, $friendly, $hasFriendly, $devdesc, $hasDevDesc) {
    $pre = if ($flow -eq 'Render') { '{0.0.0.00000000}.' } else { '{0.0.1.00000000}.' }
    $epath = 'SYSTEM\CurrentControlSet\Enum\SWD\MMDEVAPI\' + $pre + $id
    $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($epath, $true)
    if ($k -eq $null) { return }
    try {
        if ($hasFriendly) { $k.SetValue('FriendlyName', $friendly, 'String') }
        if ($hasDevDesc) { $k.SetValue('DeviceDesc', $devdesc, 'String') }
    } finally { $k.Dispose() }
}

function Restart-Audio {
    $wait = New-TimeSpan -Seconds 20
    $sc = New-Object System.ServiceProcess.ServiceController('Audiosrv')
    try {
        $deps = @()
        foreach ($d in $sc.DependentServices) {
            if ($d.ServiceName.StartsWith('AarSvc', 'OrdinalIgnoreCase')) { continue }
            $d.Refresh()
            if ($d.Status -eq 'Running' -or $d.Status -eq 'StartPending') {
                $d.Stop()
                $d.WaitForStatus('Stopped', $wait)
            }
            $deps += $d
        }
        $sc.Refresh()
        if ($sc.Status -ne 'Stopped') {
            $sc.Stop()
            $sc.WaitForStatus('Stopped', $wait)
        }
        $sc.Start()
        $sc.WaitForStatus('Running', $wait)
        foreach ($d in $deps) {
            $d.Refresh()
            if ($d.Status -eq 'Stopped') { $d.Start() }
        }
        foreach ($d in $deps) { $d.Dispose() }
    } finally { $sc.Dispose() }
}

function Set-MmsysInterfaceName($flow, $id, $name, $description) {
    if (-not ('MmsysPolicy' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MmsysPolicy {
  [DllImport("ole32.dll")]
  static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)]
  delegate int SetProp(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, int fx, ref PropertyKey key, IntPtr pv);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)]
  delegate int Release(IntPtr self);
  [StructLayout(LayoutKind.Sequential)]
  struct PropertyKey {
    public Guid fmtid;
    public uint pid;
    public PropertyKey(string g, uint p) { fmtid = new Guid(g); pid = p; }
  }
  public static void SetInterfaceName(string deviceId, string value) {
    Guid clsid = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    Guid iid = new Guid("f8679f50-850a-41cf-9c72-430f290290c8");
    IntPtr obj;
    int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out obj);
    if (hr < 0 || obj == IntPtr.Zero) throw new InvalidOperationException("0x" + hr.ToString("X8"));
    try {
      IntPtr slot = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), 12 * IntPtr.Size);
      var set = (SetProp)Marshal.GetDelegateForFunctionPointer(slot, typeof(SetProp));
      var key = new PropertyKey("b3f8fa53-0004-438e-9003-51a46e139bfc", 6);
      IntPtr pv = Marshal.AllocHGlobal(24);
      try {
        for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
        Marshal.WriteInt16(pv, 31);
        Marshal.WriteIntPtr(pv, 8, Marshal.StringToHGlobalUni(value));
        hr = set(obj, deviceId, 0, ref key, pv);
        IntPtr s = Marshal.ReadIntPtr(pv, 8);
        if (s != IntPtr.Zero) Marshal.FreeHGlobal(s);
      } finally { Marshal.FreeHGlobal(pv); }
      if (hr < 0) throw new InvalidOperationException("0x" + hr.ToString("X8"));
    } finally {
      var rel = (Release)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), 2 * IntPtr.Size), typeof(Release));
      rel(obj);
    }
  }
}
'@ -ErrorAction Stop
    }
    $label = $description
    if ([string]::IsNullOrEmpty($label)) { $label = $name }
    if ([string]::IsNullOrEmpty($label)) { throw 'Nom vide.' }
    $prefix = if ($flow -eq 'Render') { '{0.0.0.00000000}.' } else { '{0.0.1.00000000}.' }
    [MmsysPolicy]::SetInterfaceName($prefix + $id, $label)
}

# ---------------- main ----------------
$log = Join-Path $Dir 'AudioRename.autorestore.log'
function Write-Log($s) {
    $line = (Get-Date -Format 's') + ' ' + $s
    Add-Content -Path $log -Value $line -Encoding UTF8
    Write-Output $line
}

try {
    if ($MmsysOnly) {
        Set-MmsysInterfaceName $Flow $Id $Name $Description
        exit 0
    }
    if (-not $TestOnly) {
        $wp = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $wp.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Droits administrateur requis.'
        }
        Enable-Privileges
    }
    $all = Load-Backup (Join-Path $Dir 'AudioRename.backup.txt')
    if ($all.Count -eq 0) { Write-Log 'no-backup'; exit 1 }
    $renderList = Get-Endpoints 'Render'
    $captureList = Get-Endpoints 'Capture'
    $applied = 0
    $failed = 0
    $pending = @()
    foreach ($b in $all) {
        try {
            $r = Resolve-Endpoint $b $renderList $captureList
            if ($r -eq $null) { throw 'appareil introuvable' }
            $full = if ([string]::IsNullOrEmpty($b.Description)) { $b.Name } else { $b.Name + ' (' + $b.Description + ')' }
            if ($TestOnly) {
                Write-Output ("WOULD {0} {1} via={2} Friendly='{3}' Interface='{4}' EnumFriendly='{5}'" -f $b.Flow, $r.Id, $r.How, $b.Name, $b.Description, $full)
            } else {
                $vals = @{}
                $vals[$FRIENDLY] = if ($b.Name -ne $null) { $b.Name } else { '' }
                $vals[$IFACE] = if ($b.Description -ne $null) { $b.Description } else { '' }
                if ($b.HasAlias) { $vals[$ALIAS] = if ($b.Alias -ne $null) { $b.Alias } else { '' } }
                if ($b.HasInterface) { $vals[$DEVDESC] = if ($b.InterfaceName -ne $null) { $b.InterfaceName } else { '' } }
                Write-MMDevice $b.Flow $r.Id $vals
                $ef = if ($b.HasFriendly) { if ($b.FriendlyName -ne $null) { $b.FriendlyName } else { '' } } else { $full }
                $ed = if ($b.DeviceDesc -ne $null) { $b.DeviceDesc } else { '' }
                Write-EnumValues $b.Flow $r.Id $ef $b.HasFriendly $ed $b.HasDeviceDesc
                $pending += New-Object PSObject -Property @{ Flow = $b.Flow; Id = $r.Id; Name = $b.Name; Description = $b.Description }
                Write-Output ("OK {0} {1} via={2}" -f $b.Flow, $r.Id, $r.How)
            }
            $applied++
        } catch {
            $failed++
            Write-Output ("FAIL {0}/{1} : {2}" -f $b.Flow, $b.Id, $_.Exception.Message)
        }
    }
    if (-not $TestOnly -and $applied -gt 0) {
        try { Restart-Audio } catch { Write-Output ("RESTART-FAIL : " + $_.Exception.Message) }
        foreach ($m in $pending) {
            try {
                Set-MmsysInterfaceName $m.Flow $m.Id $m.Name $m.Description
                Write-Output ("MMSYS {0} {1}" -f $m.Flow, $m.Id)
            } catch {
                $failed++
                Write-Output ("MMSYS-FAIL {0}/{1} : {2}" -f $m.Flow, $m.Id, $_.Exception.Message)
            }
        }
    }
    Write-Log ("applied=" + $applied + " failed=" + $failed)
    if ($failed -eq 0) { exit 0 } else { exit 2 }
} catch {
    Write-Log ('ERROR:' + $_.Exception.Message)
    exit 2
}
