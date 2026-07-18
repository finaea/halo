#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Halo's processes as self-contained Release builds into <root>\bin\.

.DESCRIPTION
    Per native-widget-implementation-plan.md (portable-first policy, §15): published
    binaries live inside the project folder, self-contained, so no external runtime
    dependency at run time. This script does NOT touch anything outside the project
    folder and does NOT require admin rights.

    Publishes:
      src\Halo.Collector -> bin\Halo.Collector\
      src\Halo.Widgets    -> bin\Halo.Widgets\
      src\Halo.Settings   -> bin\Halo.Settings\   (only if the csproj exists yet)

    Stops on the first failure with a clear message (no partial silent success).
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$binRoot = Join-Path $root 'bin'

Write-Host "Halo publish — project root: $root" -ForegroundColor Cyan

function Publish-HaloProject {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$OutDir,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $ProjectPath)) {
        Write-Host "  [skip] $Name — project file not found at $ProjectPath" -ForegroundColor Yellow
        return
    }

    Write-Host "  Publishing $Name ..." -ForegroundColor Cyan
    Write-Host "    project: $ProjectPath"
    Write-Host "    output : $OutDir"

    & dotnet publish "$ProjectPath" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "$OutDir"
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        Write-Host ""
        Write-Host "FAILED: dotnet publish for $Name exited with code $exitCode." -ForegroundColor Red
        Write-Host "Aborting publish-halo.ps1 — fix the error above and re-run." -ForegroundColor Red
        exit $exitCode
    }

    Write-Host "  OK: $Name published to $OutDir" -ForegroundColor Green
    Write-Host ""
}

$collectorProj = Join-Path $root 'src\Halo.Collector\Halo.Collector.csproj'
$widgetsProj   = Join-Path $root 'src\Halo.Widgets\Halo.Widgets.csproj'
$settingsProj  = Join-Path $root 'src\Halo.Settings\Halo.Settings.csproj'

Publish-HaloProject -ProjectPath $collectorProj -OutDir (Join-Path $binRoot 'Halo.Collector') -Name 'Halo.Collector'
Publish-HaloProject -ProjectPath $widgetsProj   -OutDir (Join-Path $binRoot 'Halo.Widgets')   -Name 'Halo.Widgets'

if (Test-Path -LiteralPath $settingsProj) {
    Publish-HaloProject -ProjectPath $settingsProj -OutDir (Join-Path $binRoot 'Halo.Settings') -Name 'Halo.Settings'
}
else {
    Write-Host "  [skip] Halo.Settings — csproj not created yet ($settingsProj)" -ForegroundColor Yellow
}

Write-Host "All published projects succeeded." -ForegroundColor Green
Write-Host "Binaries under: $binRoot"
