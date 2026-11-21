<#
.SYNOPSIS
    Publie Accord (WinUI 3 + Watcher + Admin, .NET 10 win-x64 self-contained, 0 prérequis)
    en partageant un seul dossier publish/ sans dupliquer le runtime, puis compile l'installateur Inno Setup 6.
#>
[CmdletBinding()]
param(
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$PublishDir = Join-Path $Root 'publish'
$StageWatcher = Join-Path $Root 'obj\stage-watcher'
$StageAdmin = Join-Path $Root 'obj\stage-admin'
$DistDir = Join-Path $Root 'dist'
$IssPath = Join-Path $Root 'installer\Accord.iss'

function Invoke-Checked {
    param([string]$File, [string[]]$Arguments)
    Write-Host ">> $File $($Arguments -join ' ')" -ForegroundColor Cyan
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Commande échouée ($LASTEXITCODE) : $File"
    }
}

function Find-Iscc {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "ISCC.exe (Inno Setup 6) introuvable."
}

Write-Host '=== 1. Arrêt des instances éventuelles et nettoyage ===' -ForegroundColor Green
Get-Process -Name 'Accord', 'Accord.Watcher', 'Accord.Admin' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($PublishDir, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 250

foreach ($dir in @($PublishDir, $StageWatcher, $StageAdmin)) {
    if (Test-Path -LiteralPath $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $PublishDir, $DistDir | Out-Null

Write-Host '=== 2. Publication de Accord.App (WinUI 3 + WindowsAppSDK self-contained) ===' -ForegroundColor Green
Invoke-Checked 'dotnet' @(
    'publish', (Join-Path $Root 'src\Accord.App\Accord.App.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '-p:Platform=x64',
    '--self-contained', 'true',
    '-p:PublishReadyToRun=false',
    '-o', $PublishDir
)

Write-Host '=== 3. Publication de Accord.Watcher et Accord.Admin (runtime partagé) ===' -ForegroundColor Green
Invoke-Checked 'dotnet' @(
    'publish', (Join-Path $Root 'src\Accord.Watcher\Accord.Watcher.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '-p:Platform=x64',
    '--self-contained', 'true',
    '-o', $StageWatcher
)
Invoke-Checked 'dotnet' @(
    'publish', (Join-Path $Root 'src\Accord.Admin\Accord.Admin.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '-p:Platform=x64',
    '--self-contained', 'true',
    '-o', $StageAdmin
)

# Fusionne les binaires propres à Watcher et Admin + éventuelles DLL manquantes dans publish/
foreach ($stage in @($StageWatcher, $StageAdmin)) {
    Get-ChildItem -Path $stage -File | ForEach-Object {
        $dest = Join-Path $PublishDir $_.Name
        $isOwnArtifact = $_.Name -like 'Accord.Watcher.*' -or $_.Name -like 'Accord.Admin.*'
        if ($isOwnArtifact -or -not (Test-Path -LiteralPath $dest)) {
            Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        }
    }
}

Write-Host '=== 4. Nettoyage des langues satellites inutiles et des symboles .pdb ===' -ForegroundColor Green
$keepLocales = @('fr', 'fr-FR', 'en', 'en-US')
$localePattern = '^[a-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$'
$nonLocaleDirs = @('Assets', 'Microsoft.UI.Xaml', 'runtimes', 'win-x64', 'logs', 'ipc')

Get-ChildItem -Path $PublishDir -Directory | ForEach-Object {
    $name = $_.Name
    if ($nonLocaleDirs -contains $name) { return }
    if ($name -match $localePattern -and ($keepLocales -notcontains $name)) {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}

Get-ChildItem -Path $PublishDir -Filter '*.pdb' -Recurse -File | Remove-Item -Force

# Nettoyage des dossiers de staging
Remove-Item -LiteralPath $StageWatcher, $StageAdmin -Recurse -Force -ErrorAction SilentlyContinue

$pubSizeMb = [Math]::Round(((Get-ChildItem -Path $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Dossier publish/ prêt : $pubSizeMb Mo" -ForegroundColor Yellow

if (-not $SkipInstaller) {
    Write-Host '=== 5. Compilation de l''installateur Inno Setup 6 ===' -ForegroundColor Green
    $iscc = Find-Iscc
    Invoke-Checked $iscc @($IssPath)
    $setupFile = Get-ChildItem -Path $DistDir -Filter 'Accord-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($setupFile) {
        $setupMb = [Math]::Round($setupFile.Length / 1MB, 1)
        Write-Host "Installateur généré : $($setupFile.FullName) ($setupMb Mo)" -ForegroundColor Green
    }
}
