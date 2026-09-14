#requires -Version 5.1
<#
.SYNOPSIS
    Undoes tools\install-dev.ps1: stops Halo and removes the two scheduled tasks.

.DESCRIPTION
    Stop order matters. Halo.Widgets owns a watchdog that restarts the collector task
    within a few seconds, so widgets goes first, then the collector, then the PresentMon
    service the collector spawned (it does NOT die with its parent, and an orphan keeps
    dist\app\presentmon\ locked against the next build).

    Task removal is delegated to the same CLI verb the installer's uninstaller uses:
    dist\app\Halo.Settings.exe --unregister-autostart. That is the one step that needs
    admin, so it is the only thing that elevates.

    Nothing else is touched. PawnIO is a shared driver (FanControl, LibreHardwareMonitor
    and HWiNFO use the same one) and is never removed from here; %LOCALAPPDATA%\Halo keeps
    your config and layouts unless you delete it yourself.

.PARAMETER AppDir
    The layout the tasks point at. Defaults to <repo>\dist\app.
#>

[CmdletBinding()]
param(
    [string]$AppDir
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $AppDir) { $AppDir = Join-Path $root 'dist\app' }
$settingsExe = Join-Path $AppDir 'Halo.Settings.exe'

Write-Host "Halo dev uninstall" -ForegroundColor Cyan
Write-Host "    layout: $AppDir" -ForegroundColor DarkGray
Write-Host ""

# --- 1. Widgets first: it is the watchdog that would resurrect the collector ---
Write-Host "==> Stopping Halo processes" -ForegroundColor Cyan
Get-Process -Name 'Halo.Widgets' -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null      # clean exit saves the widget layout
    if (-not $_.WaitForExit(3000)) { $_ | Stop-Process -Force }
}

try { Stop-ScheduledTask -TaskPath '\Halo\' -TaskName 'Widgets' -ErrorAction Stop } catch { }
try { Stop-ScheduledTask -TaskPath '\Halo\' -TaskName 'Collector' -ErrorAction Stop } catch { }
Get-Process -Name 'Halo.Collector', 'Halo.Settings' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# PresentMon children, scoped by image path so a separately installed Intel PresentMon is
# never touched. An elevated child started by the collector task reads back with an empty
# ExecutablePath from this unelevated shell - say so instead of silently skipping it.
$pmAll     = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'PresentMon%'" -ErrorAction SilentlyContinue)
$pmOurs    = @($pmAll | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($AppDir, [StringComparison]::OrdinalIgnoreCase) })
$pmOpaque  = @($pmAll | Where-Object { -not $_.ExecutablePath })
foreach ($pm in $pmOurs) { Stop-Process -Id $pm.ProcessId -Force -ErrorAction SilentlyContinue }
if ($pmOurs.Count -gt 0) { Write-Host "    stopped $($pmOurs.Count) PresentMon child process(es)" -ForegroundColor DarkGray }
if ($pmOpaque.Count -gt 0) {
    Write-Host "    WARNING: $($pmOpaque.Count) PresentMon process(es) run elevated and cannot be stopped from here." -ForegroundColor Yellow
    Write-Host "    They keep dist\app\presentmon\ locked. Re-run this from an elevated shell if a build fails on a locked file." -ForegroundColor Yellow
}
Write-Host "    OK" -ForegroundColor Green

# --- 2. Remove the tasks (needs admin) ---
Write-Host "==> Removing \Halo\Collector and \Halo\Widgets (UAC prompt)" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $settingsExe)) {
    Write-Host "    $settingsExe is gone - remove the tasks by hand:" -ForegroundColor Yellow
    Write-Host '      schtasks /Delete /TN "\Halo\Collector" /F' -ForegroundColor Yellow
    Write-Host '      schtasks /Delete /TN "\Halo\Widgets" /F' -ForegroundColor Yellow
    exit 1
}
try {
    $p = Start-Process -FilePath $settingsExe -ArgumentList '--unregister-autostart' -Verb RunAs -Wait -PassThru
}
catch [System.ComponentModel.Win32Exception] {
    Write-Host "    UAC prompt was declined - the tasks are still registered." -ForegroundColor Yellow
    exit 1
}
if ($p.ExitCode -ne 0) {
    Write-Host "    FAILED: --unregister-autostart exited $($p.ExitCode)." -ForegroundColor Red
    exit 1
}
Write-Host "    OK" -ForegroundColor Green

Write-Host ""
Write-Host "Done. Nothing global left (docs\install-footprint.md)." -ForegroundColor Green
Write-Host "Kept on purpose: PawnIO (shared driver) and %LOCALAPPDATA%\Halo (config and layouts)." -ForegroundColor DarkGray
