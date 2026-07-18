#requires -Version 5.1
<#
.SYNOPSIS
    Fully removes Halo's global registrations (native-widget-implementation-plan.md §15).

.DESCRIPTION
    Stops Halo's processes (and the bundled PresentMon console child), then removes
    every global registration listed in global-installs.md's "Added by Halo" table:
      - Scheduled task "\Halo\Collector"
      - HKCU Run value "HaloWidgets"
      - The optional Windows Defender exclusion, if one was added by install-halo.ps1
    Each step is idempotent / safe to re-run and tolerates "already removed / never
    existed" without failing the script.

    Self-elevates: scheduled-task deletion and the Defender exclusion check both
    require admin rights.

    Pre-existing system components (PawnIO, FanControl, .NET SDK) are never touched —
    see global-installs.md's "Pre-existing" table.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Self-elevate if not already running as Administrator.
# ---------------------------------------------------------------------------
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "uninstall-halo.ps1 needs Administrator rights (scheduled task removal). Elevating..." -ForegroundColor Cyan

    $scriptPath = $MyInvocation.MyCommand.Path
    $argList = New-Object System.Collections.Generic.List[string]
    $argList.Add('-NoProfile')
    $argList.Add('-ExecutionPolicy'); $argList.Add('Bypass')
    $argList.Add('-File'); $argList.Add("`"$scriptPath`"")

    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList ($argList -join ' ')
    exit
}

$root = Split-Path -Parent $PSScriptRoot
Write-Host "Halo uninstall — project root: $root" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------------------
# 1. Stop Halo processes + any bundled PresentMon console child.
# ---------------------------------------------------------------------------
Write-Host "Stopping Halo processes ..." -ForegroundColor Cyan

$haloProcessNames = @('Halo.Widgets', 'Halo.Collector', 'Halo.Settings')
foreach ($procName in $haloProcessNames) {
    $procs = Get-Process -Name $procName -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Write-Host "  $procName : stopped ($($procs.Count) process(es))" -ForegroundColor Green
    }
    else {
        Write-Host "  $procName : not running" -ForegroundColor DarkGray
    }
}

# Bundled PresentMon console client (tools\presentmon\PresentMon-<version>-x64.exe) —
# no service to stop, just the child process(es) the collector may have left behind.
$pmProcs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'PresentMon-*-x64' }
if ($pmProcs) {
    $pmProcs | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "  PresentMon child : stopped ($($pmProcs.Count) process(es))" -ForegroundColor Green
}
else {
    Write-Host "  PresentMon child : not running" -ForegroundColor DarkGray
}
Write-Host ""

# ---------------------------------------------------------------------------
# 2. Scheduled task \Halo\Collector.
# ---------------------------------------------------------------------------
Write-Host "Removing scheduled task \Halo\Collector ..." -ForegroundColor Cyan

$taskStatus = 'not present'
try {
    & schtasks.exe /Delete /TN "\Halo\Collector" /F 2>$null | Out-Null
    $taskStatus = 'removed'
}
catch {
    $taskStatus = 'not present'
}
Write-Host "  Collector task : $taskStatus" -ForegroundColor $(if ($taskStatus -eq 'removed') { 'Green' } else { 'DarkGray' })
Write-Host ""

# ---------------------------------------------------------------------------
# 3. HKCU Run value HaloWidgets.
# ---------------------------------------------------------------------------
Write-Host "Removing HKCU Run value HaloWidgets ..." -ForegroundColor Cyan

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueStatus = 'not present'
$existingRunValue = Get-ItemProperty -Path $runKey -Name 'HaloWidgets' -ErrorAction SilentlyContinue
if ($existingRunValue) {
    Remove-ItemProperty -Path $runKey -Name 'HaloWidgets' -ErrorAction SilentlyContinue
    $stillThere = Get-ItemProperty -Path $runKey -Name 'HaloWidgets' -ErrorAction SilentlyContinue
    if ($stillThere) {
        $runValueStatus = 'FAILED to remove'
    }
    else {
        $runValueStatus = 'removed'
    }
}
Write-Host "  HaloWidgets Run value : $runValueStatus" -ForegroundColor $(if ($runValueStatus -eq 'removed') { 'Green' } elseif ($runValueStatus -eq 'not present') { 'DarkGray' } else { 'Red' })
Write-Host ""

# ---------------------------------------------------------------------------
# 4. Optional Windows Defender exclusion (only if install-halo.ps1 -AddDefenderExclusion
#    was used).
# ---------------------------------------------------------------------------
Write-Host "Checking for Windows Defender exclusion ..." -ForegroundColor Cyan

$defenderStatus = 'not present'
try {
    $mpPref = Get-MpPreference -ErrorAction Stop
    if ($mpPref.ExclusionPath -and ($mpPref.ExclusionPath -contains $root)) {
        Remove-MpPreference -ExclusionPath $root -ErrorAction Stop
        $defenderStatus = 'removed'
    }
    else {
        $defenderStatus = 'not present'
    }
}
catch {
    $defenderStatus = 'not present (Defender module unavailable or exclusion not set)'
}
Write-Host "  Defender exclusion : $defenderStatus" -ForegroundColor $(if ($defenderStatus -eq 'removed') { 'Green' } else { 'DarkGray' })
Write-Host ""

# ---------------------------------------------------------------------------
# Summary.
# ---------------------------------------------------------------------------
Write-Host '=== Halo uninstall summary ===' -ForegroundColor Cyan
Write-Host "Collector scheduled task \Halo\Collector : $taskStatus"
Write-Host "HKCU Run value HaloWidgets                : $runValueStatus"
Write-Host "Windows Defender exclusion                : $defenderStatus"
Write-Host "Intel PresentMon Service                   : n/a — was never registered (bundled console client only)"
Write-Host ""
Write-Host "Folder can now be deleted for complete removal." -ForegroundColor Green
Write-Host "PawnIO (C:\Program Files\PawnIO) belongs to FanControl — untouched." -ForegroundColor Yellow
