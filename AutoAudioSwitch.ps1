# Bascule auto du son et du micro. La surveillance tourne dans Bascule\Accord.exe.
# Regles :
#   Pixel Buds connectes -> sortie + micro Pixel, pas le profil Hands-Free
#   Razer allume (lien 2.4 GHz reel via le dongle HID) -> sortie + micro Razer
#   Sinon -> Philips Evnia
# Si les deux casques sont actifs, le dernier connecte gagne.
# Au demarrage : Pixel, puis Razer, puis ecran.
# -Enable / -Disable / -Query : case de la fenetre Accord.
# -TestOnly : affiche le choix sans changer le son.
param(
    [switch]$Enable,
    [switch]$Disable,
    [switch]$Query,
    [switch]$TestOnly
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$LauncherPath = Join-Path $ScriptDir 'Bascule\Accord.exe'
$StartupDir = [Environment]::GetFolderPath('Startup')
$ShortcutPath = Join-Path $StartupDir 'Accord.lnk'
$OldShortcutPath = Join-Path $StartupDir 'AudioRename Bascule.lnk'

function Get-WatcherProcesses {
    Get-CimInstance Win32_Process -Filter "Name='Accord.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.ExecutablePath -and ($_.ExecutablePath -ieq $LauncherPath)
    }
}

function Get-LegacyWatchers {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -and
        $_.CommandLine -like '*AutoAudioSwitch.ps1*' -and
        $_.CommandLine -notlike '*-Enable*' -and
        $_.CommandLine -notlike '*-Disable*' -and
        $_.CommandLine -notlike '*-Query*' -and
        $_.CommandLine -notlike '*-TestOnly*'
    }
}

function Stop-Watchers {
    Get-LegacyWatchers | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Get-WatcherProcesses | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Enable-AutoSwitch {
    if (-not (Test-Path -LiteralPath $LauncherPath)) { throw "Introuvable : $LauncherPath" }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $LauncherPath
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = $ScriptDir
    $shortcut.WindowStyle = 7
    $shortcut.Description = 'Accord'
    $shortcut.IconLocation = $LauncherPath + ',0'
    $shortcut.Save()
    if (Test-Path -LiteralPath $OldShortcutPath) { Remove-Item -LiteralPath $OldShortcutPath -Force }
    Get-LegacyWatchers | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (-not (Get-WatcherProcesses)) {
        Start-Process -FilePath $LauncherPath -WorkingDirectory $ScriptDir
    }
}

function Disable-AutoSwitch {
    if (Test-Path -LiteralPath $ShortcutPath) { Remove-Item -LiteralPath $ShortcutPath -Force }
    if (Test-Path -LiteralPath $OldShortcutPath) { Remove-Item -LiteralPath $OldShortcutPath -Force }
    Stop-Watchers
}

if ($Query) {
    if (Test-Path -LiteralPath $ShortcutPath) { exit 0 } else { exit 1 }
}
if ($Enable) { Enable-AutoSwitch; exit 0 }
if ($Disable) { Disable-AutoSwitch; exit 0 }
if ($TestOnly) {
    & $LauncherPath -TestOnly
    exit $LASTEXITCODE
}

Get-LegacyWatchers | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (-not (Get-WatcherProcesses)) {
    Start-Process -FilePath $LauncherPath -WorkingDirectory $ScriptDir
}
exit 0
