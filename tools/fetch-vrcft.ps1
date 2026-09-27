# Builds the VRCFaceTracking SDK/Core reference assemblies for one supported version.
#
# VRCFT 5.4.x ships only through Steam and publishes no packages, so the module is compiled
# against assemblies built from VRCFT's source at the commit that release was cut from.
# Versions and commits live in vrcft-versions.json; the first entry is what releases target.
#
# Prints the output directory, suitable for -p:VrcftPath=...

[CmdletBinding()]
param(
    # Defaults to the first (newest) entry in vrcft-versions.json.
    [string]$Version = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$versions = Get-Content (Join-Path $PSScriptRoot "vrcft-versions.json") -Raw | ConvertFrom-Json
$entry = if ($Version) { $versions | Where-Object version -eq $Version } else { $versions[0] }
if (-not $entry) { throw "VRCFT $Version is not listed in vrcft-versions.json." }

if (-not $OutDir) { $OutDir = Join-Path $root ".vrcft\$($entry.version)" }
$assemblies = "VRCFaceTracking.SDK.dll", "VRCFaceTracking.Core.dll"

if (-not ($assemblies | Where-Object { -not (Test-Path (Join-Path $OutDir $_)) })) {
    Write-Host "VRCFT $($entry.version) already built at $OutDir" -ForegroundColor DarkGray
    return $OutDir
}

$src = Join-Path ([IO.Path]::GetTempPath()) "vrcft-src-$($entry.ref.Substring(0, 10))"
if (Test-Path $src) { Remove-Item $src -Recurse -Force }
New-Item -ItemType Directory -Force $src | Out-Null

Write-Host "Fetching VRCFT $($entry.version) ($($entry.ref))..."
& git -C $src init --quiet | Out-Host
& git -C $src fetch --quiet --depth 1 https://github.com/benaclejames/VRCFaceTracking.git $entry.ref | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Fetching VRCFT source failed." }
& git -C $src -c advice.detachedHead=false checkout --quiet FETCH_HEAD | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Checking out VRCFT source failed." }

Write-Host "Building VRCFT $($entry.version)..."
$project = Join-Path $src "VRCFaceTracking.Core\VRCFaceTracking.Core.csproj"
$bin = Join-Path $src "out"
& dotnet build $project -c Release --nologo -v quiet -o $bin | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Building VRCFT failed." }

New-Item -ItemType Directory -Force $OutDir | Out-Null
foreach ($name in $assemblies) { Copy-Item (Join-Path $bin $name) $OutDir -Force }
Remove-Item $src -Recurse -Force

return $OutDir
