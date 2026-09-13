#requires -Version 5.1
<#
.SYNOPSIS
    Registers Halo's autostart against a build-from-source layout (dist\app).

.DESCRIPTION
    The installer's job, for people who cloned and ran tools\build.ps1 instead of
    downloading the setup exe. It registers the same two scheduled tasks the installer
    registers, pointing at dist\app:

        \Halo\Collector   RunLevel Highest   (full sensors)
        \Halo\Widgets     RunLevel Limited   (medium integrity, like any user app)

    The task definition itself lives in exactly one place -- AutostartManager in
    Halo.Settings -- and this script just calls the same CLI verb the installer calls:
    dist\app\Halo.Settings.exe --register-autostart.

    Run this from a NORMAL shell. It elevates only that one call, so widgets starts at
    medium integrity the way it is supposed to (the old install-halo.ps1 self-elevated the
    whole script and then had to launch widgets through explorer.exe to shed the elevation).

    No HKCU Run value is written any more. --register-autostart deletes a leftover
    "HaloWidgets" one if it finds it.

.PARAMETER AppDir
    The layout to register. Defaults to <repo>\dist\app.

.EXAMPLE
    tools\build.ps1
    tools\install-dev.ps1
#>

[CmdletBinding()]
param(
    [string]$AppDir
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $AppDir) { $AppDir = Join-Path $root 'dist\app' }

$settingsExe  = Join-Path $AppDir 'Halo.Settings.exe'
$collectorExe = Join-Path $AppDir 'Halo.Collector.exe'
$widgetsExe   = Join-Path $AppDir 'Halo.Widgets.exe'

foreach ($exe in @($settingsExe, $collectorExe, $widgetsExe)) {
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Host "ABORT: $exe not found." -ForegroundColor Red
        Write-Host "Build it first:  tools\build.ps1" -ForegroundColor Red
        exit 1
    }
}

Write-Host "Halo dev install" -ForegroundColor Cyan
Write-Host "    layout: $AppDir" -ForegroundColor DarkGray
Write-Host ""

# --- 1. Register the two tasks (the only step that needs admin) ---
Write-Host "==> Registering \Halo\Collector and \Halo\Widgets (UAC prompt)" -ForegroundColor Cyan
try {
    $p = Start-Process -FilePath $settingsExe -ArgumentList '--register-autostart' -Verb RunAs -Wait -PassThru
}
catch [System.ComponentModel.Win32Exception] {
    Write-Host "    UAC prompt was declined - nothing was registered." -ForegroundColor Yellow
    exit 1
}
if ($p.ExitCode -ne 0) {
    Write-Host "    FAILED: --register-autostart exited $($p.ExitCode)." -ForegroundColor Red
    Write-Host "    740 = not elevated, 1 = error (run it from an elevated console to see the message)." -ForegroundColor Red
    exit 1
}
Write-Host "    OK" -ForegroundColor Green

# --- 2. Start the collector through its task, so it runs elevated -> full sensors ---
Write-Host "==> Starting the collector task" -ForegroundColor Cyan
& schtasks.exe /Run /TN "\Halo\Collector" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "    WARNING: schtasks /Run exited $LASTEXITCODE. It will still start at next logon." -ForegroundColor Yellow
}
else { Write-Host "    OK" -ForegroundColor Green }

# --- 3. Start widgets in this (medium-integrity) session ---
Write-Host "==> Starting Halo.Widgets" -ForegroundColor Cyan
if (Get-Process -Name 'Halo.Widgets' -ErrorAction SilentlyContinue) {
    Write-Host "    already running - left alone (use tools\redeploy-halo.ps1 to bounce it)" -ForegroundColor DarkGray
}
else {
    Start-Process -FilePath $widgetsExe
    Write-Host "    OK" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Global footprint: two scheduled tasks under \Halo\. See docs\global-installs.md." -ForegroundColor Green
Write-Host "Remove with: tools\uninstall-dev.ps1"
