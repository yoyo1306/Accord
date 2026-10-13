# Bascule auto du son et du micro, branchee sur les noms AudioRename.
# Regles (inchangees) :
#   Pixel Buds connectes -> sortie + micro Pixel, pas le profil Hands-Free
#   Razer allume (lien 2.4 GHz reel via le dongle HID) -> sortie + micro Razer
#   Sinon -> Philips Evnia
# Si les deux casques sont actifs, le dernier connecte gagne.
# Au demarrage : Pixel, puis Razer, puis ecran.
# Les appareils sont ceux de AudioRename.backup.txt (GUID, puis "Nom (Description)").
# Si Windows remet un nom pilote, le nom enregistre est reecrit sans couper le son.
# -Enable / -Disable / -Query : case de la fenetre AudioRename.
# -TestOnly : affiche le choix sans changer le son.
param(
    [switch]$Enable,
    [switch]$Disable,
    [switch]$Query,
    [switch]$TestOnly
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BackupPath = Join-Path $ScriptDir 'AudioRename.backup.txt'
$LauncherPath = Join-Path $ScriptDir 'Bascule\AudioRename.exe'
$StartupDir = [Environment]::GetFolderPath('Startup')
$ShortcutPath = Join-Path $StartupDir 'AudioRename Bascule.lnk'
$AppDir = Join-Path $env:LOCALAPPDATA 'AutoAudioSwitch'
$LogPath = Join-Path $AppDir 'log.txt'
$StatePath = Join-Path $AppDir 'state.txt'
$PollSeconds = 2

function Get-WatcherProcesses {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -and
        $_.CommandLine -like '*AutoAudioSwitch.ps1*' -and
        $_.CommandLine -notlike '*-Enable*' -and
        $_.CommandLine -notlike '*-Disable*' -and
        $_.CommandLine -notlike '*-Query*' -and
        $_.CommandLine -notlike '*-TestOnly*'
    }
}

function Enable-AutoSwitch {
    if (-not (Test-Path -LiteralPath $LauncherPath)) { throw "Introuvable : $LauncherPath" }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $LauncherPath
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = $ScriptDir
    $shortcut.WindowStyle = 7
    $shortcut.Description = 'AudioRename'
    $shortcut.IconLocation = $LauncherPath + ',0'
    $shortcut.Save()
    if (-not (Get-WatcherProcesses)) {
        Start-Process -FilePath $LauncherPath -WorkingDirectory $ScriptDir -WindowStyle Hidden
    }
}

function Disable-AutoSwitch {
    if (Test-Path -LiteralPath $ShortcutPath) { Remove-Item -LiteralPath $ShortcutPath -Force }
    Get-WatcherProcesses | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

if ($Query) {
    if (Test-Path -LiteralPath $ShortcutPath) { exit 0 } else { exit 1 }
}
if ($Enable) { Enable-AutoSwitch; exit 0 }
if ($Disable) { Disable-AutoSwitch; exit 0 }

$script:InstanceMutex = New-Object System.Threading.Mutex($false, 'Local\AutoAudioSwitch')
try {
    if (-not $script:InstanceMutex.WaitOne(0)) { exit 0 }
} catch [System.Threading.AbandonedMutexException] {
}

Import-Module (Join-Path $ScriptDir 'Modules\AudioDeviceCmdlets\3.1.0.2\AudioDeviceCmdlets.psd1') -ErrorAction Stop
New-Item -ItemType Directory -Path $AppDir -Force | Out-Null
if (-not ('AutoAudioSwitch.RazerDongle' -as [type])) {
    Add-Type -Path (Join-Path $ScriptDir 'RazerDongle.cs') -ErrorAction Stop
}
if ((Test-Path $LogPath) -and ((Get-Item $LogPath).Length -gt 512KB)) {
    Move-Item $LogPath ($LogPath + '.old') -Force
}

function Write-Log([string]$Message) {
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
    if ($TestOnly) { Write-Output $line }
}

function Load-Backup([string]$Path) {
    $list = @()
    if (-not (Test-Path -LiteralPath $Path)) { return $list }
    $cur = $null
    foreach ($line in [System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::UTF8)) {
        if ($line -eq '---') {
            if ($null -ne $cur -and $cur.Id -ne '') { $list += $cur }
            $cur = $null
            continue
        }
        $eq = $line.IndexOf('=')
        if ($eq -lt 0) { continue }
        if ($null -eq $cur) {
            $cur = New-Object PSObject -Property @{ Flow = ''; Id = ''; Name = ''; Description = ''; FriendlyName = '' }
        }
        $key = $line.Substring(0, $eq)
        $val = $line.Substring($eq + 1)
        switch ($key) {
            'Flow' { $cur.Flow = $val }
            'Id' { $cur.Id = $val }
            'Name' { $cur.Name = $val }
            'Description' { $cur.Description = $val }
            'FriendlyName' { $cur.FriendlyName = $val }
        }
    }
    return $list
}

function Get-SavedDisplay($Record) {
    if ($Record.FriendlyName) { return [string]$Record.FriendlyName }
    if ($Record.Description) { return ([string]$Record.Name) + ' (' + $Record.Description + ')' }
    return [string]$Record.Name
}

function Get-SavedLabel($Record) {
    $display = Get-SavedDisplay $Record
    $prefix = [string]$Record.Name + ' ('
    if ($display.StartsWith($prefix) -and $display.EndsWith(')')) {
        return $display.Substring($prefix.Length, $display.Length - $prefix.Length - 1)
    }
    if ($Record.Description) { return [string]$Record.Description }
    return [string]$Record.Name
}

function Set-SavedInterfaceName([string]$Flow, [string]$Id, [string]$Label) {
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
    $prefix = if ($Flow -eq 'Render') { '{0.0.0.00000000}.' } else { '{0.0.1.00000000}.' }
    [MmsysPolicy]::SetInterfaceName($prefix + $Id, $Label)
}

function Repair-SavedNames {
    if (-not $script:NameRepairState) { $script:NameRepairState = @{} }
    $devices = @(Get-AudioDevice -List)
    $now = [DateTime]::UtcNow
    foreach ($rec in @(Load-Backup $BackupPath)) {
        if (-not $rec.Id) { continue }
        $prefix = if ($rec.Flow -eq 'Render') { '{0.0.0.00000000}.' } else { '{0.0.1.00000000}.' }
        $fullId = $prefix + $rec.Id
        $live = $null
        foreach ($d in $devices) {
            if ([string]$d.ID -eq $fullId) { $live = $d; break }
        }
        if (-not $live) { continue }
        $want = Get-SavedDisplay $rec
        $shown = [string]$live.Name
        if (-not $want -or $shown -eq $want) { continue }
        $state = $script:NameRepairState[$fullId]
        if ($state -and $state.Live -eq $shown -and $now -lt $state.Next) { continue }
        $label = Get-SavedLabel $rec
        if (-not $label) { continue }
        try {
            Set-SavedInterfaceName $rec.Flow $rec.Id $label
            Write-Log ("Name restored " + $rec.Flow + " " + $shown + " -> " + $want)
        } catch {
            Write-Log ("Name restore failed " + $rec.Flow + " " + $rec.Id + " " + $_.Exception.Message)
        }
        $script:NameRepairState[$fullId] = @{ Live = $shown; Next = $now.AddMinutes(5) }
    }
}

function Find-Live($Devices, [string]$Type, [string[]]$Include, [string[]]$Exclude = @()) {
    foreach ($d in @($Devices)) {
        if ($d.Type -ne $Type) { continue }
        $name = [string]$d.Name
        $ok = $true
        foreach ($inc in $Include) { if ($name -notlike $inc) { $ok = $false; break } }
        if (-not $ok) { continue }
        foreach ($exc in $Exclude) { if ($name -like $exc) { $ok = $false; break } }
        if ($ok) { return $d }
    }
    return $null
}

function Find-ByRecord($Devices, [string]$Type, $Record) {
    if ($null -eq $Record) { return $null }
    $prefix = if ($Record.Flow -eq 'Render') { '{0.0.0.00000000}.' } else { '{0.0.1.00000000}.' }
    $id = $prefix + $Record.Id
    foreach ($d in @($Devices)) {
        if ($d.Type -eq $Type -and [string]$d.ID -eq $id) { return $d }
    }
    $friendly = [string]$Record.Name
    if ($Record.Description) { $friendly = $Record.Name + ' (' + $Record.Description + ')' }
    foreach ($d in @($Devices)) {
        if ($d.Type -eq $Type -and [string]$d.Name -eq $friendly) { return $d }
    }
    return $null
}

function Get-RoleRecords($Records) {
    $render = @($Records | Where-Object { $_.Flow -eq 'Render' })
    $capture = @($Records | Where-Object { $_.Flow -eq 'Capture' })
    return [PSCustomObject]@{
        PixelPlay = @($render | Where-Object { $_.Description -like '*Pixel Buds*' -and $_.Description -notlike '*Hands-Free*' } | Select-Object -First 1)[0]
        PixelRec  = @($capture | Where-Object { $_.Description -like '*Pixel Buds*' } | Select-Object -First 1)[0]
        RazerPlay = @($render | Where-Object { $_.Name -eq 'Razer' -or ($_.Description -like '*BlackShark*' -and $_.Description -notlike '*Chat*') } | Select-Object -First 1)[0]
        RazerRec  = @($capture | Where-Object { $_.Name -eq 'Razer' -or $_.Description -like '*BlackShark*' } | Select-Object -First 1)[0]
        Screen    = @($render | Where-Object { $_.Name -eq 'Philips' -or $_.Description -like '*Evnia*' } | Select-Object -First 1)[0]
    }
}

function Resolve-Role($Devices, [string]$Type, $Record, [string[]]$Include, [string[]]$Exclude = @()) {
    $hit = Find-ByRecord $Devices $Type $Record
    if ($hit) { return $hit }
    return Find-Live $Devices $Type $Include $Exclude
}

function Test-PixelBluetoothConnected {
    $bt = Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -like '*Pixel Buds*' -and $_.Status -eq 'OK' }
    return [bool]$bt
}

function Test-RazerWirelessConnected {
    $state = [AutoAudioSwitch.RazerDongle]::GetWirelessState()
    return ($state -eq [AutoAudioSwitch.RazerHeadsetState]::Connected)
}

function Get-HeadsetPresence {
    $devices = @(Get-AudioDevice -List)
    $roles = Get-RoleRecords (Load-Backup $BackupPath)
    $pixelPlay = Resolve-Role $devices 'Playback' $roles.PixelPlay @('*Pixel Buds*') @('*Hands-Free*')
    $pixelRec = Resolve-Role $devices 'Recording' $roles.PixelRec @('*Pixel Buds*') @('*Hands-Free*')
    if (-not $pixelRec) {
        $pixelRec = Resolve-Role $devices 'Recording' $roles.PixelRec @('*Pixel Buds*')
    }
    $razerPlay = Resolve-Role $devices 'Playback' $roles.RazerPlay @('*BlackShark*') @('*Chat*', '*Hands-Free*')
    $razerRec = Resolve-Role $devices 'Recording' $roles.RazerRec @('*BlackShark*')
    $screen = Resolve-Role $devices 'Playback' $roles.Screen @('*Evnia*')
    if (-not $screen) { $screen = Find-Live $devices 'Playback' @('*Philips*') }

    return [PSCustomObject]@{
        PixelOn = ($null -ne $pixelPlay) -and (Test-PixelBluetoothConnected)
        RazerOn = ($null -ne $razerPlay) -and (Test-RazerWirelessConnected)
        PixelPlay = $pixelPlay
        PixelRec = $pixelRec
        RazerPlay = $razerPlay
        RazerRec = $razerRec
        Screen = $screen
    }
}

function Set-DefaultBoth($Device) {
    if (-not $Device) { return }
    Set-AudioDevice -ID $Device.ID -DefaultOnly | Out-Null
    Set-AudioDevice -ID $Device.ID -CommunicationOnly | Out-Null
}

function Apply-Profile([string]$Name, $Presence) {
    switch ($Name) {
        'Razer' {
            if (-not $Presence.RazerPlay) { return }
            Set-DefaultBoth $Presence.RazerPlay
            if ($Presence.RazerRec) { Set-DefaultBoth $Presence.RazerRec }
            Write-Log ("Profile Razer | out=" + $Presence.RazerPlay.Name + " | mic=" + $Presence.RazerRec.Name)
        }
        'Pixel' {
            if (-not $Presence.PixelPlay) { return }
            Set-DefaultBoth $Presence.PixelPlay
            if ($Presence.PixelRec) { Set-DefaultBoth $Presence.PixelRec }
            Write-Log ("Profile Pixel | out=" + $Presence.PixelPlay.Name + " | mic=" + $Presence.PixelRec.Name)
        }
        default {
            if (-not $Presence.Screen) { return }
            Set-DefaultBoth $Presence.Screen
            Write-Log ("Profile Screen | out=" + $Presence.Screen.Name)
        }
    }
    Set-Content -Path $StatePath -Value $Name -Encoding UTF8
}

function Resolve-StartupProfile($Presence) {
    if ($Presence.PixelOn) { return 'Pixel' }
    if ($Presence.RazerOn) { return 'Razer' }
    return 'Screen'
}

function Ensure-Applied([string]$Name, $Presence) {
    $play = Get-AudioDevice -Playback
    $rec = Get-AudioDevice -Recording
    $need = $false
    switch ($Name) {
        'Razer' {
            if ($Presence.RazerPlay -and $play.ID -ne $Presence.RazerPlay.ID) { $need = $true }
            if ($Presence.RazerRec -and $rec.ID -ne $Presence.RazerRec.ID) { $need = $true }
        }
        'Pixel' {
            if ($Presence.PixelPlay -and $play.ID -ne $Presence.PixelPlay.ID) { $need = $true }
            if ($Presence.PixelRec -and $rec.ID -ne $Presence.PixelRec.ID) { $need = $true }
        }
        default {
            if ($Presence.Screen -and $play.ID -ne $Presence.Screen.ID) { $need = $true }
        }
    }
    if ($need) { Apply-Profile $Name $Presence }
}

function Format-Device($Device) {
    if (-not $Device) { return '(absent)' }
    return $Device.Name + ' ' + $Device.ID
}

$presence = Get-HeadsetPresence
$active = Resolve-StartupProfile $presence
if ($TestOnly) {
    Write-Output ("PixelOn=" + [bool]$presence.PixelOn + " RazerOn=" + [bool]$presence.RazerOn + " -> " + $active)
    Write-Output ("PixelPlay " + (Format-Device $presence.PixelPlay))
    Write-Output ("PixelRec " + (Format-Device $presence.PixelRec))
    Write-Output ("RazerPlay " + (Format-Device $presence.RazerPlay))
    Write-Output ("RazerRec " + (Format-Device $presence.RazerRec))
    Write-Output ("Screen " + (Format-Device $presence.Screen))
    exit 0
}

Write-Log 'Watcher started (AudioRename names + Pixel BT + Razer HID)'
Write-Log ("Startup: Pixel=" + [bool]$presence.PixelOn + " RazerHID=" + [bool]$presence.RazerOn + " -> " + $active)
Repair-SavedNames
Apply-Profile $active $presence
$pixelWas = [bool]$presence.PixelOn
$razerWas = [bool]$presence.RazerOn

while ($true) {
    try {
        Repair-SavedNames
        $presence = Get-HeadsetPresence
        $pixelOn = [bool]$presence.PixelOn
        $razerOn = [bool]$presence.RazerOn
        if ($razerOn -and -not $razerWas) {
            $active = 'Razer'
            Apply-Profile $active $presence
        }
        elseif ($pixelOn -and -not $pixelWas) {
            $active = 'Pixel'
            Apply-Profile $active $presence
        }
        elseif ($active -eq 'Razer' -and -not $razerOn) {
            $active = if ($pixelOn) { 'Pixel' } else { 'Screen' }
            Apply-Profile $active $presence
        }
        elseif ($active -eq 'Pixel' -and -not $pixelOn) {
            $active = if ($razerOn) { 'Razer' } else { 'Screen' }
            Apply-Profile $active $presence
        }
        else {
            Ensure-Applied $active $presence
        }
        $pixelWas = $pixelOn
        $razerWas = $razerOn
    }
    catch {
        Write-Log ("Error: " + $_.Exception.Message)
    }
    Start-Sleep -Seconds $PollSeconds
}
