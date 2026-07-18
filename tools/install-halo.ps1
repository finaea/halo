#requires -Version 5.1
<#
.SYNOPSIS
    Registers Halo's autostart (Collector scheduled task + Widgets HKCU Run value).

.DESCRIPTION
    Per native-widget-implementation-plan.md §11 and §15 (portable-first policy):
    only two global *registrations* are created, both pointing at files inside this
    project folder:
      - Scheduled task "\Halo\Collector" (highest privileges, at logon, no UAC prompt)
      - HKCU Run value "HaloWidgets"
    Every registration this script creates has a matching row in global-installs.md.

    Idempotent — safe to re-run; existing registrations are overwritten in place.

    Self-elevates: scheduled-task creation with /RL HIGHEST and the (optional) Defender
    exclusion both require admin rights.

.PARAMETER AddDefenderExclusion
    Optional, OFF by default. Adds a Windows Defender exclusion for the whole project
    folder via Add-MpPreference. This is the user's call, not a default — Defender's ML
    heuristic quarantined Halo.Collector.csproj once (2026-07-19,
    Trojan:Win32/Bearfoos.A!ml, a false positive on plain MSBuild XML). Only pass this
    switch if repeated false-positive quarantines become disruptive.
#>

[CmdletBinding()]
param(
    [switch]$AddDefenderExclusion
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Self-elevate if not already running as Administrator.
# ---------------------------------------------------------------------------
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "install-halo.ps1 needs Administrator rights (scheduled task /RL HIGHEST). Elevating..." -ForegroundColor Cyan

    $scriptPath = $MyInvocation.MyCommand.Path
    $argList = New-Object System.Collections.Generic.List[string]
    $argList.Add('-NoProfile')
    $argList.Add('-ExecutionPolicy'); $argList.Add('Bypass')
    $argList.Add('-File'); $argList.Add("`"$scriptPath`"")
    if ($AddDefenderExclusion) {
        $argList.Add('-AddDefenderExclusion')
    }

    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList ($argList -join ' ')
    exit
}

# ---------------------------------------------------------------------------
# Resolve project root and binaries: published (Release, self-contained) wins
# over the Debug build if both exist.
# ---------------------------------------------------------------------------
$root = Split-Path -Parent $PSScriptRoot

$collectorPublished = Join-Path $root 'bin\Halo.Collector\Halo.Collector.exe'
$collectorDebug      = Join-Path $root 'src\Halo.Collector\bin\Debug\net9.0\win-x64\Halo.Collector.exe'
$widgetsPublished   = Join-Path $root 'bin\Halo.Widgets\Halo.Widgets.exe'
$widgetsDebug        = Join-Path $root 'src\Halo.Widgets\bin\Debug\net9.0\win-x64\Halo.Widgets.exe'

if (Test-Path -LiteralPath $collectorPublished) {
    $collectorExe = $collectorPublished
    $collectorKind = 'published'
}
elseif (Test-Path -LiteralPath $collectorDebug) {
    $collectorExe = $collectorDebug
    $collectorKind = 'Debug build'
}
else {
    Write-Host "ABORT: no Halo.Collector.exe found." -ForegroundColor Red
    Write-Host "  Checked published: $collectorPublished"
    Write-Host "  Checked Debug    : $collectorDebug"
    Write-Host "Build the Collector first (dotnet build, or tools\publish-halo.ps1) and re-run." -ForegroundColor Red
    exit 1
}

if (Test-Path -LiteralPath $widgetsPublished) {
    $widgetsExe = $widgetsPublished
    $widgetsKind = 'published'
}
elseif (Test-Path -LiteralPath $widgetsDebug) {
    $widgetsExe = $widgetsDebug
    $widgetsKind = 'Debug build'
}
else {
    Write-Host "ABORT: no Halo.Widgets.exe found." -ForegroundColor Red
    Write-Host "  Checked published: $widgetsPublished"
    Write-Host "  Checked Debug    : $widgetsDebug"
    Write-Host "Build the Widgets process first (dotnet build, or tools\publish-halo.ps1) and re-run." -ForegroundColor Red
    exit 1
}

Write-Host "Halo install — project root: $root" -ForegroundColor Cyan
Write-Host "  Collector exe ($collectorKind): $collectorExe"
Write-Host "  Widgets exe   ($widgetsKind): $widgetsExe"
Write-Host ""

# ---------------------------------------------------------------------------
# Scheduled task: \Halo\Collector — highest privileges, at logon, no UAC prompt.
# ---------------------------------------------------------------------------
Write-Host "Registering scheduled task \Halo\Collector ..." -ForegroundColor Cyan

& schtasks.exe /Create /TN "\Halo\Collector" /TR "$collectorExe" /SC ONLOGON /RL HIGHEST /F | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED to create scheduled task \Halo\Collector (schtasks exit $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}
Write-Host "  OK: task created/updated." -ForegroundColor Green

& schtasks.exe /Run /TN "\Halo\Collector" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  WARNING: task created but failed to start now (schtasks /Run exit $LASTEXITCODE). It will still run at next logon." -ForegroundColor Yellow
}
else {
    Write-Host "  OK: task started now." -ForegroundColor Green
}
Write-Host ""

# ---------------------------------------------------------------------------
# HKCU Run value: HaloWidgets
# ---------------------------------------------------------------------------
Write-Host "Registering HKCU Run value HaloWidgets ..." -ForegroundColor Cyan

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $runKey -Name 'HaloWidgets' -Value $widgetsExe -PropertyType String -Force | Out-Null
Write-Host "  OK: $runKey\HaloWidgets = $widgetsExe" -ForegroundColor Green
Write-Host ""

# ---------------------------------------------------------------------------
# Optional: Windows Defender exclusion for the whole project folder.
# ---------------------------------------------------------------------------
if ($AddDefenderExclusion) {
    Write-Host "Adding Windows Defender exclusion for $root ..." -ForegroundColor Cyan
    try {
        Add-MpPreference -ExclusionPath $root
        Write-Host "  OK: Defender exclusion added." -ForegroundColor Green
        Write-Host "  SECURITY NOTE: this excludes the entire Halo project folder from Defender" -ForegroundColor Yellow
        Write-Host "  real-time scanning. This was requested because Defender's ML heuristic" -ForegroundColor Yellow
        Write-Host "  quarantined Halo.Collector.csproj on 2026-07-19 (Trojan:Win32/Bearfoos.A!ml," -ForegroundColor Yellow
        Write-Host "  a false positive on plain MSBuild XML). The exclusion is your call, not a" -ForegroundColor Yellow
        Write-Host "  default — remove it with uninstall-halo.ps1 or Remove-MpPreference." -ForegroundColor Yellow
    }
    catch {
        Write-Host "  WARNING: could not add Defender exclusion: $_" -ForegroundColor Yellow
    }
    Write-Host ""
}
else {
    Write-Host "Defender exclusion: skipped (default). Pass -AddDefenderExclusion to enable it." -ForegroundColor DarkGray
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Start widgets for this session. This script is running elevated (required
# for the scheduled task above), but widgets should normally run as a plain
# user process. Try the Explorer ShellExecute COM trick to launch it
# non-elevated; if that fails, fall back to an elevated start for tonight
# and rely on the Run key for a properly non-elevated start at next logon.
# ---------------------------------------------------------------------------
Write-Host "Starting Halo.Widgets for this session ..." -ForegroundColor Cyan
$widgetsStarted = $false
try {
    $shellApp = New-Object -ComObject 'Shell.Application'
    $widgetsDir = Split-Path -Parent $widgetsExe
    $shellApp.ShellExecute($widgetsExe, '', $widgetsDir, 'open', 1)
    Write-Host "  OK: started non-elevated via Explorer." -ForegroundColor Green
    $widgetsStarted = $true
}
catch {
    Write-Host "  Explorer non-elevated launch failed: $_" -ForegroundColor Yellow
}

if (-not $widgetsStarted) {
    Write-Host "  NOTE: starting Halo.Widgets elevated instead (this process is elevated)." -ForegroundColor Yellow
    Write-Host "  Running widgets elevated is acceptable for tonight only — it is not the" -ForegroundColor Yellow
    Write-Host "  normal mode (widgets should run as a plain user process). It will start" -ForegroundColor Yellow
    Write-Host "  correctly non-elevated at the next logon via the Run key registered above." -ForegroundColor Yellow
    try {
        Start-Process -FilePath $widgetsExe
        Write-Host "  OK: started (elevated)." -ForegroundColor Green
    }
    catch {
        Write-Host "  WARNING: could not start Halo.Widgets now: $_" -ForegroundColor Yellow
        Write-Host "  It will still start at next logon via the Run key." -ForegroundColor Yellow
    }
}
Write-Host ""

# ---------------------------------------------------------------------------
# Summary — matches global-installs.md "Added by Halo" table.
# ---------------------------------------------------------------------------
Write-Host '=== Halo install summary ===' -ForegroundColor Cyan
Write-Host "Intel PresentMon Service   : n/a — console client bundled at tools\presentmon\, no global footprint"
Write-Host "Collector autostart        : scheduled task \Halo\Collector -> $collectorExe"
Write-Host "Widgets autostart          : HKCU Run\HaloWidgets -> $widgetsExe"
if ($AddDefenderExclusion) {
    Write-Host "Defender exclusion         : ON -> $root"
}
else {
    Write-Host "Defender exclusion         : off (default)"
}
Write-Host ""
Write-Host "Install complete. See global-installs.md for the full manifest." -ForegroundColor Green
