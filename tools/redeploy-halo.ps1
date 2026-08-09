#requires -Version 5.1
<#
.SYNOPSIS
    One-command dev cycle for Halo: stop -> (publish) -> start, in the correct order.

.DESCRIPTION
    Halo runs as two cooperating processes and getting them to pick up a fresh build by
    hand is fiddly, because:
      * The tray "Exit Halo" stops Halo.Widgets ONLY - the collector keeps running as the
        \Halo\Collector scheduled task and keeps the bin\ binaries locked.
      * Halo.Widgets has a watchdog that re-launches the collector task within ~1-5 s of it
        going stale, so stopping the collector while widgets is still up just resurrects it.
    So the safe order is always: stop widgets FIRST (kills the watchdog), THEN stop the
    collector task, THEN publish, THEN start the collector task (elevated, for full sensors),
    THEN start widgets.

    This script does that. It touches nothing outside the project folder and needs no admin
    (controlling your own \Halo\ task is allowed for the owning account).

.PARAMETER RestartOnly
    Skip the publish step - just bounce both processes on whatever is already in bin\.
    Handy when you only changed config, or already published.

.PARAMETER Verify
    After starting, dump the collector's shared memory and print the drive.* metrics so you
    can confirm a newly-added drive is live.

.EXAMPLE
    tools\redeploy-halo.ps1
        Full cycle: stop -> publish -> start.

.EXAMPLE
    tools\redeploy-halo.ps1 -RestartOnly
        Just restart both processes (no rebuild).

.EXAMPLE
    tools\redeploy-halo.ps1 -Verify
        Full cycle, then print drive metrics to confirm.
#>

[CmdletBinding()]
param(
    [switch]$RestartOnly,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

$root         = Split-Path -Parent $PSScriptRoot
$taskPath     = '\Halo\'
$taskName     = 'Collector'
$collectorExe = Join-Path $root 'bin\Halo.Collector\Halo.Collector.exe'
$widgetsExe   = Join-Path $root 'bin\Halo.Widgets\Halo.Widgets.exe'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# --- 1. Stop widgets FIRST (this also removes the collector watchdog) ---
Step 'Stopping Halo.Widgets'
Get-Process Halo.Widgets -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null   # let it exit cleanly (saves widget layout)
    if (-not $_.WaitForExit(3000)) { $_ | Stop-Process -Force }
}

# --- 2. Stop the collector task + any stray collector process (frees bin\ locks) ---
Step 'Stopping collector (\Halo\Collector task)'
try { Stop-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction Stop }
catch { Write-Host '    (task not running or not installed - continuing)' -ForegroundColor DarkGray }
Get-Process Halo.Collector -ErrorAction SilentlyContinue | Stop-Process -Force

# The bundled PresentMon service/console are CHILD processes of the collector but do NOT
# die with it. An orphan keeps tools\presentmon\ locked, which blocks a later publish and
# any attempt to move or delete the project folder.
#
# Attribution caveat: the service inherits the collector's elevation, so from the normal
# (non-elevated) redeploy its ExecutablePath reads back EMPTY and it cannot be killed.
# Never silently skip that case - filtering on path alone would make this whole block a
# no-op in exactly the mode this script is documented to run in. Report it instead.
$pmAll = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'PresentMon%'" -ErrorAction SilentlyContinue)
$pmOurs    = @($pmAll | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) })
$pmForeign = @($pmAll | Where-Object { $_.ExecutablePath -and -not $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) })
$pmOpaque  = @($pmAll | Where-Object { -not $_.ExecutablePath })   # unreadable => higher integrity than us

foreach ($pm in $pmOurs) { Stop-Process -Id $pm.ProcessId -Force -ErrorAction SilentlyContinue }
if ($pmOurs.Count -gt 0) { Write-Host "    stopped $($pmOurs.Count) PresentMon child process(es)" -ForegroundColor DarkGray }
if ($pmForeign.Count -gt 0) {
    Write-Host "    left $($pmForeign.Count) PresentMon process(es) alone - outside this project (not ours)" -ForegroundColor DarkGray
}
if ($pmOpaque.Count -gt 0) {
    Write-Host "    WARNING: $($pmOpaque.Count) PresentMon process(es) run elevated and cannot be stopped from here." -ForegroundColor Yellow
    Write-Host "    They keep tools\presentmon\ locked. If publish fails on a locked file, re-run" -ForegroundColor Yellow
    Write-Host "    this script from an elevated shell." -ForegroundColor Yellow
}

# wait for the collector image to actually be unlocked before we overwrite it
if (-not $RestartOnly -and (Test-Path -LiteralPath $collectorExe)) {
    for ($i = 0; $i -lt 20; $i++) {
        try { $fs = [IO.File]::Open($collectorExe, 'Open', 'ReadWrite', 'None'); $fs.Close(); break }
        catch { Start-Sleep -Milliseconds 250 }
    }
}

# --- 3. Publish (unless -RestartOnly) ---
if ($RestartOnly) {
    Step 'Skipping publish (-RestartOnly)'
} else {
    Step 'Publishing self-contained build into bin\'
    & (Join-Path $PSScriptRoot 'publish-halo.ps1')
    if ($LASTEXITCODE -ne 0) { throw "publish-halo.ps1 failed (exit $LASTEXITCODE)" }
}

# --- 4. Start the collector via its task (runs elevated -> full sensors) ---
Step 'Starting collector (\Halo\Collector task)'
try {
    Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction Stop
} catch {
    Write-Host '    Task not installed - run tools\install-halo.ps1 once to register autostart.' -ForegroundColor Yellow
    Write-Host '    Starting collector directly instead (NOTE: unelevated -> temps/CPU/storage read N/A).' -ForegroundColor Yellow
    if (Test-Path -LiteralPath $collectorExe) { Start-Process $collectorExe }
}

# --- 5. Start widgets ---
Step 'Starting Halo.Widgets'
if (Test-Path -LiteralPath $widgetsExe) { Start-Process $widgetsExe }
else { Write-Host "    $widgetsExe not found - publish first (run without -RestartOnly)." -ForegroundColor Yellow }

# --- 6. Optional verification ---
if ($Verify -and (Test-Path -LiteralPath $collectorExe)) {
    Step 'Verifying drive metrics (give the collector a moment to register)'
    Start-Sleep -Seconds 2
    $lines = & $collectorExe --dump | Select-String 'drive\.'
    if ($lines) { $lines | ForEach-Object { Write-Host "    $_" } }
    else { Write-Host '    no drive.* metrics yet - re-run: bin\Halo.Collector\Halo.Collector.exe --dump' -ForegroundColor Yellow }
}

Write-Host 'Done.' -ForegroundColor Green
