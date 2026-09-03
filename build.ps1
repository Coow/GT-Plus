<#
.SYNOPSIS
    Builds release binaries of GT Plus for every supported platform.

.DESCRIPTION
    Reads the current version from version.json, bumps it, then publishes a
    self-contained single-file build for each runtime identifier into
    dist/<version>/<rid>/. The executable is renamed to GTPlus-<version>.

    version.json is only written back after every platform has built
    successfully, so a failed build does not consume a version number.

.PARAMETER Bump
    Which part of the semantic version to increase: Major, Minor, Patch or None.
    Defaults to Patch.

.PARAMETER SetVersion
    Use this exact version instead of bumping (e.g. -SetVersion 1.2.0).

.PARAMETER Runtime
    Runtime identifiers to build. Defaults to all supported platforms.

.PARAMETER Configuration
    MSBuild configuration. Defaults to Release.

.PARAMETER NoArchive
    Skip creating the .zip / .tar.gz archives.

.PARAMETER NoBundleNative
    Leave native libraries (Skia, HarfBuzz) beside the executable instead of
    bundling them inside the single-file host.

.EXAMPLE
    .\build.ps1
    Bumps the patch version and builds all platforms.

.EXAMPLE
    .\build.ps1 -Bump Minor
    Bumps the minor version (patch resets to 0) and builds all platforms.

.EXAMPLE
    .\build.ps1 -SetVersion 1.0.0 -Rid win-x64
    Builds only Windows x64 as version 1.0.0.
#>
[CmdletBinding()]
param(
    [ValidateSet('Major', 'Minor', 'Patch', 'None')]
    [string] $Bump = 'Patch',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $SetVersion,

    [Alias('Rid')]
    [string[]] $Runtime = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64'),

    [string] $Configuration = 'Release',

    [switch] $NoArchive,

    [switch] $NoBundleNative
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot    = $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src/VmixGtPlus/VmixGtPlus.csproj'
$VersionFile = Join-Path $RepoRoot 'version.json'
$DistRoot    = Join-Path $RepoRoot 'dist'
$ExeBaseName = 'GTPlus'

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# --- Resolve the version to build -------------------------------------------

if (-not (Test-Path $ProjectPath)) {
    throw "Project not found: $ProjectPath"
}

$current = '0.0.0'
if (Test-Path $VersionFile) {
    $current = (Get-Content $VersionFile -Raw | ConvertFrom-Json).version
}

if ($SetVersion) {
    $version = $SetVersion
}
else {
    $parts = $current.Split('.')
    $major = [int] $parts[0]
    $minor = [int] $parts[1]
    $patch = [int] $parts[2]

    switch ($Bump) {
        'Major' { $major++; $minor = 0; $patch = 0 }
        'Minor' { $minor++; $patch = 0 }
        'Patch' { $patch++ }
        'None'  { }
    }
    $version = "$major.$minor.$patch"
}

Write-Step "Building GT Plus $version ($Configuration) - previous version was $current"

# --- Publish each platform ---------------------------------------------------

$outputRoot = Join-Path $DistRoot $version
if (Test-Path $outputRoot) {
    Write-Host "    Clearing existing output $outputRoot"
    Remove-Item $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$bundleNative = (-not $NoBundleNative).ToString().ToLowerInvariant()
$built = @()

foreach ($rt in $Runtime) {
    Write-Step "Publishing $rt"

    $ridDir = Join-Path $outputRoot $rt

    $publishArgs = @(
        'publish', $ProjectPath,
        '-c', $Configuration,
        '-r', $rt,
        '--self-contained', 'true',
        '-o', $ridDir,
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=$bundleNative",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:Version=$version",
        "-p:FileVersion=$version.0",
        "-p:InformationalVersion=$version",
        '--nologo',
        '-v', 'minimal'
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rt (exit code $LASTEXITCODE)"
    }

    # Rename the published host to GTPlus-<version>. The single-file host does
    # not care about its own filename, and the assembly name stays VmixGtPlus
    # so avares:// resource URIs keep resolving.
    $isWin = $rt.StartsWith('win')
    $srcExe    = Join-Path $ridDir ($isWin ? 'VmixGtPlus.exe' : 'VmixGtPlus')
    $dstName   = if ($isWin) { "$ExeBaseName-$version.exe" } else { "$ExeBaseName-$version" }

    if (-not (Test-Path $srcExe)) {
        throw "Expected published executable not found: $srcExe"
    }
    Move-Item $srcExe (Join-Path $ridDir $dstName) -Force

    # Strip anything that has no business in a release folder.
    Get-ChildItem $ridDir -Include '*.pdb', '*.log' -Recurse -File |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $built += [pscustomobject]@{ Rid = $rt; Dir = $ridDir; Exe = $dstName; Windows = $isWin }
    Write-Host "    -> $ridDir\$dstName" -ForegroundColor Green
}

# --- Archives ----------------------------------------------------------------

if (-not $NoArchive) {
    foreach ($b in $built) {
        if ($b.Windows) {
            $archive = Join-Path $outputRoot "$ExeBaseName-$version-$($b.Rid).zip"
            Write-Step "Packing $(Split-Path $archive -Leaf)"
            Compress-Archive -Path (Join-Path $b.Dir '*') -DestinationPath $archive -Force
        }
        else {
            $archive = Join-Path $outputRoot "$ExeBaseName-$version-$($b.Rid).tar.gz"
            Write-Step "Packing $(Split-Path $archive -Leaf)"
            & tar -czf $archive -C $b.Dir '.'
            if ($LASTEXITCODE -ne 0) {
                throw "tar failed for $($b.Rid) (exit code $LASTEXITCODE)"
            }
        }
    }
}

# --- Persist the version only after everything succeeded ---------------------

@{ version = $version } | ConvertTo-Json | Set-Content $VersionFile -Encoding utf8

Write-Host ''
Write-Step "GT Plus $version built to $outputRoot"
foreach ($b in $built) {
    Write-Host ("    {0,-12} {1}" -f $b.Rid, $b.Exe)
}
if ($built | Where-Object { -not $_.Windows }) {
    Write-Host ''
    Write-Host 'Note: Windows cannot set the Unix executable bit. On Linux/macOS run:' -ForegroundColor Yellow
    Write-Host "      chmod +x $ExeBaseName-$version" -ForegroundColor Yellow
}
