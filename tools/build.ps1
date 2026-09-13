#requires -Version 5.1
<#
.SYNOPSIS
    Builds Halo's release layout into dist\app, and optionally the Inno Setup installer
    and the portable zip.

.DESCRIPTION
    All three exes are published self-contained into ONE folder so they share a single
    copy of the .NET runtime (packaging plan P1). Each exe keeps its own
    <name>.deps.json / <name>.runtimeconfig.json, so nothing collides; the runtime files
    themselves are byte-identical because global.json pins the SDK.

    dist\app is exactly what the installer copies to Program Files\Halo, and exactly what
    the portable zip contains:

        Halo.Collector.exe  Halo.Widgets.exe  Halo.Settings.exe  + shared runtime
        assets\fonts\*.ttf  assets\halo.ico
        presentmon\PresentMonAPI2.dll  PresentMonService.exe  LICENSE.txt
        redist\PawnIO_setup.exe
        LICENSE  NOTICE.md  THIRD-PARTY-NOTICES.md

    Needs no admin. Touches nothing outside the repo except the NuGet restore (which goes
    to the project-local cache in tools\nuget-cache) and, on the first run, the PawnIO
    download.

.PARAMETER Installer
    Also compile installer\halo.iss with ISCC -> dist\Halo-Setup-<version>.exe.

.PARAMETER Zip
    Also pack dist\app into dist\Halo-<version>-win-x64.zip, with a portable.marker added
    inside the zip (so the unpacked copy keeps config and logs next to the exes).

.PARAMETER Sign
    Stub. Code signing is out of scope for v1 (packaging plan P8); this switch exists so
    the hook is in one place when a certificate shows up.

.PARAMETER NoReadyToRun
    Publish without ReadyToRun. R2R is on by default: it costs ~14 MB on disk and buys
    faster cold start. See the measurement in the packaging plan's P1 row.

.PARAMETER Clean
    Delete dist\app before publishing. Without it the publish overwrites in place, which
    is faster but can leave a file behind that a previous build produced.

.EXAMPLE
    tools\build.ps1
        Publish dist\app only.

.EXAMPLE
    tools\build.ps1 -Clean -Installer -Zip
        Full release build: dist\app, dist\Halo-Setup-<ver>.exe, dist\Halo-<ver>-win-x64.zip.
#>

[CmdletBinding()]
param(
    [switch]$Installer,
    [switch]$Zip,
    [switch]$Sign,
    [switch]$NoReadyToRun,
    [switch]$Clean,
    [string]$Output
)

$ErrorActionPreference = 'Stop'

# --- PawnIO redistributable: pinned release, verified by hash ------------------------
# GPL-2.0-or-later, invoked as a separate program (THIRD-PARTY-NOTICES.md). Kept out of
# git; fetched once into installer\redist\ and copied into dist\app\redist\ so the
# installer component and the Settings "Install PawnIO" button both find it at
# <app root>\redist\PawnIO_setup.exe (Halo.Shared.Paths.RedistDir).
$PawnIoVersion = '2.2.0'
$PawnIoUrl     = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
$PawnIoSha256  = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'

$root    = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $root 'dist'
$appDir  = if ($Output) { $Output } else { Join-Path $distDir 'app' }

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# Version: one source of truth, Directory.Build.props.
# ---------------------------------------------------------------------------
$propsPath = Join-Path $root 'Directory.Build.props'
$props = Get-Content -LiteralPath $propsPath -Raw
if ($props -notmatch '<Version>([^<]+)</Version>') {
    throw "No <Version> element in $propsPath"
}
$version = $Matches[1].Trim()

# ---------------------------------------------------------------------------
# dotnet: PATH first, then DOTNET_ROOT, then the per-user and machine-wide installs.
# A per-user SDK (winget's --scope user, or the dotnet-install script) is not on PATH
# for a fresh shell, and the published exes need DOTNET_ROOT pointed at it too.
# ---------------------------------------------------------------------------
function Resolve-Dotnet {
    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')

    foreach ($c in $candidates) {
        if (-not (Test-Path -LiteralPath $c)) { continue }
        $sdks = & $c --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0) { continue }
        if ($sdks | Where-Object { $_ -match '^\s*10\.' }) { return $c }
    }
    return $null
}

$dotnet = Resolve-Dotnet
if (-not $dotnet) {
    Write-Host "ABORT: no .NET 10 SDK found." -ForegroundColor Red
    Write-Host "  Install it with:  winget install Microsoft.DotNet.SDK.10" -ForegroundColor Red
    Write-Host "  Checked PATH, `$env:DOTNET_ROOT, %LOCALAPPDATA%\Microsoft\dotnet, %ProgramFiles%\dotnet." -ForegroundColor Red
    exit 1
}
# The publish itself shells out to apphost/crossgen from the same root; keep them aligned.
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host "Halo build $version" -ForegroundColor Cyan
Note "repo   : $root"
Note "dotnet : $dotnet"
Note "output : $appDir"
Write-Host ""

# ---------------------------------------------------------------------------
# 1. Publish the three exes into one folder.
# ---------------------------------------------------------------------------
if ($Clean -and (Test-Path -LiteralPath $appDir)) {
    Step "Cleaning $appDir"
    Remove-Item -LiteralPath $appDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $appDir | Out-Null

$r2r = -not $NoReadyToRun
$projects = @(
    @{ Name = 'Halo.Collector'; Path = 'src\Halo.Collector\Halo.Collector.csproj' }
    @{ Name = 'Halo.Widgets';   Path = 'src\Halo.Widgets\Halo.Widgets.csproj' }
    @{ Name = 'Halo.Settings';  Path = 'src\Halo.Settings\Halo.Settings.csproj' }
)

foreach ($p in $projects) {
    Step "Publishing $($p.Name) (self-contained, R2R=$r2r)"
    $proj = Join-Path $root $p.Path
    if (-not (Test-Path -LiteralPath $proj)) { throw "Project not found: $proj" }

    & $dotnet publish $proj `
        -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=$($r2r.ToString().ToLowerInvariant()) `
        -p:Version=$version `
        -o $appDir `
        --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($p.Name) (exit $LASTEXITCODE)" }
    Ok "$($p.Name) published"
}
Write-Host ""

# ---------------------------------------------------------------------------
# 2. Payload the exes read from their own folder at run time (Halo.Shared.Paths).
#    The csprojs already copy most of this, but the layout contract is this script's
#    job — a missing Content item in a csproj must not silently ship a broken folder.
# ---------------------------------------------------------------------------
Step 'Copying assets, PresentMon SDK and licences'

function Copy-Tree($from, $to, $what) {
    if (-not (Test-Path -LiteralPath $from)) { Warn "$what missing at $from - skipped"; return $false }
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Copy-Item -Path (Join-Path $from '*') -Destination $to -Recurse -Force
    return $true
}

[void](Copy-Tree (Join-Path $root 'assets') (Join-Path $appDir 'assets') 'assets')
$pmOk = Copy-Tree (Join-Path $root 'tools\presentmon\sdk') (Join-Path $appDir 'presentmon') 'PresentMon SDK'
if ($pmOk) {
    # The P/Invoke header is a source artefact, not a runtime file.
    Remove-Item -LiteralPath (Join-Path $appDir 'presentmon\PresentMonAPI.h') -Force -ErrorAction SilentlyContinue
    $pmDll = Join-Path $appDir 'presentmon\PresentMonAPI2.dll'
    $pmSvc = Join-Path $appDir 'presentmon\PresentMonService.exe'
    if (-not (Test-Path -LiteralPath $pmDll) -or -not (Test-Path -LiteralPath $pmSvc)) {
        Warn 'PresentMonAPI2.dll / PresentMonService.exe are missing - the frame pipeline will report no-sdk.'
    }
}

foreach ($f in @('LICENSE', 'NOTICE.md', 'THIRD-PARTY-NOTICES.md')) {
    $src = Join-Path $root $f
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $appDir $f) -Force }
    else { Warn "$f missing at repo root - not shipped" }
}
Ok 'payload copied'
Write-Host ""

# ---------------------------------------------------------------------------
# 3. PawnIO redistributable.
# ---------------------------------------------------------------------------
Step "PawnIO $PawnIoVersion redistributable"
$redistSrcDir = Join-Path $root 'installer\redist'
$redistSrc    = Join-Path $redistSrcDir 'PawnIO_setup.exe'

function Test-PawnIoHash($path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $PawnIoSha256
}

if (Test-PawnIoHash $redistSrc) {
    Note "cached and verified: $redistSrc"
}
else {
    if (Test-Path -LiteralPath $redistSrc) {
        Warn 'cached copy does not match the pinned SHA256 - re-downloading'
        Remove-Item -LiteralPath $redistSrc -Force
    }
    New-Item -ItemType Directory -Force -Path $redistSrcDir | Out-Null
    Note "downloading $PawnIoUrl"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $PawnIoUrl -OutFile $redistSrc -UseBasicParsing
    }
    catch {
        throw "PawnIO download failed: $_`nDownload it by hand from $PawnIoUrl into $redistSrcDir and re-run."
    }
    if (-not (Test-PawnIoHash $redistSrc)) {
        $got = (Get-FileHash -LiteralPath $redistSrc -Algorithm SHA256).Hash
        Remove-Item -LiteralPath $redistSrc -Force
        throw "PawnIO_setup.exe SHA256 mismatch. Expected $PawnIoSha256, got $got. Refusing to ship an unverified driver installer."
    }
    Ok 'downloaded and hash-verified'
}

New-Item -ItemType Directory -Force -Path (Join-Path $appDir 'redist') | Out-Null
Copy-Item -LiteralPath $redistSrc -Destination (Join-Path $appDir 'redist\PawnIO_setup.exe') -Force
Ok 'redist\PawnIO_setup.exe staged'
Write-Host ""

# ---------------------------------------------------------------------------
# 4. Signing hook (stub).
# ---------------------------------------------------------------------------
if ($Sign) {
    Step 'Signing'
    Warn 'Code signing is not implemented (packaging plan P8: v1 ships unsigned).'
    Warn 'When a certificate exists, sign here: the three exes in dist\app, then the'
    Warn 'setup exe after ISCC, e.g.'
    Warn '  signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n "<subject>" <files>'
}

# ---------------------------------------------------------------------------
# 5. Size report.
# ---------------------------------------------------------------------------
$files = Get-ChildItem -LiteralPath $appDir -Recurse -File
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Step 'Size'
Ok ("dist\app: {0:N0} files, {1:N1} MB ({2:N0} bytes), ReadyToRun={3}" -f $files.Count, ($bytes / 1MB), $bytes, $r2r)
Write-Host ""

# ---------------------------------------------------------------------------
# 6. Installer.
# ---------------------------------------------------------------------------
function Resolve-Iscc {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

if ($Installer) {
    Step 'Compiling installer'
    $iscc = Resolve-Iscc
    if (-not $iscc) {
        Write-Host "ABORT: ISCC.exe (Inno Setup 6) not found." -ForegroundColor Red
        Write-Host "  Per-user install, no admin needed:" -ForegroundColor Red
        Write-Host "    download innosetup-6.x.x.exe from https://jrsoftware.org/isdl.php" -ForegroundColor Red
        Write-Host "    innosetup-6.x.x.exe /VERYSILENT /CURRENTUSER /NORESTART" -ForegroundColor Red
        exit 1
    }
    Note "iscc: $iscc"
    $iss = Join-Path $root 'installer\halo.iss'
    if (-not (Test-Path -LiteralPath $iss)) { throw "Installer script not found: $iss" }

    & $iscc "/DHaloVersion=$version" "/DHaloAppDir=$appDir" "/O$distDir" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

    $setup = Join-Path $distDir "Halo-Setup-$version.exe"
    if (-not (Test-Path -LiteralPath $setup)) { throw "ISCC reported success but $setup does not exist" }
    Ok ("{0} ({1:N1} MB)" -f $setup, ((Get-Item -LiteralPath $setup).Length / 1MB))
    Write-Host ""
}

# ---------------------------------------------------------------------------
# 7. Portable zip. portable.marker is added to the ZIP, not to dist\app: a marker
#    sitting in dist\app would silently move the dev loop's config and logs into
#    dist\app\data and away from %LOCALAPPDATA%\Halo.
# ---------------------------------------------------------------------------
if ($Zip) {
    Step 'Packing portable zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $distDir "Halo-$version-win-x64.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($appDir, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.CreateEntry('portable.marker')
        $writer = New-Object IO.StreamWriter($entry.Open())
        $writer.Write("Halo portable mode: config and logs live in .\data next to the exes.`r`nDelete this file to use %LOCALAPPDATA%\Halo instead.`r`n")
        $writer.Dispose()
    }
    finally { $archive.Dispose() }

    Ok ("{0} ({1:N1} MB)" -f $zipPath, ((Get-Item -LiteralPath $zipPath).Length / 1MB))
    Write-Host ""
}

Write-Host "Done." -ForegroundColor Green
Write-Host "Run from source:  tools\install-dev.ps1   (registers the two scheduled tasks against dist\app)"
