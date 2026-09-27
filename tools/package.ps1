# Builds a release zip for people who just want to install the module.
#
# The model is deliberately left out: it belongs to Project Babble, so the bundled installer
# downloads it from them instead of us redistributing it.

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$VrcftPath = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$module = Join-Path $root "src\FaceTrackingClone.VrcftModule\FaceTrackingClone.VrcftModule.csproj"
$cli = Join-Path $root "src\FaceTrackingClone\FaceTrackingClone.csproj"

$version = (Get-Content (Join-Path $root "src\FaceTrackingClone.VrcftModule\module.json") -Raw | ConvertFrom-Json).Version

$buildArgs = @("-c", $Configuration, "--nologo", "-v", "quiet")
if ($VrcftPath) { $buildArgs += "-p:VrcftPath=$VrcftPath" }
& dotnet build $module @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Module build failed." }
& dotnet build $cli @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Viewer build failed." }

$moduleOut = Join-Path $root "src\FaceTrackingClone.VrcftModule\bin\$Configuration\net10.0-windows\win-x64"
$cliOut = Join-Path $root "src\FaceTrackingClone\bin\$Configuration\net10.0-windows\win-x64"

$outDir = Join-Path $root "out"
$stage = Join-Path $outDir "FaceTrackingClone"
$zip = Join-Path $outDir "FaceTrackingClone-$version.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "module") | Out-Null

# Top-level files only: that skips models\, and pdb/lib are build artefacts.
Get-ChildItem $moduleOut -File | Where-Object { $_.Extension -notin ".pdb", ".lib" } |
    Copy-Item -Destination (Join-Path $stage "module")
foreach ($name in 'ftclone.exe', 'ftclone.dll', 'ftclone.runtimeconfig.json', 'ftclone.deps.json') {
    Copy-Item (Join-Path $cliOut $name) (Join-Path $stage "module")
}
Copy-Item (Join-Path $PSScriptRoot "package\*") $stage
Copy-Item (Join-Path $root "LICENSE") $stage

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Packaged $zip ($mb MB)" -ForegroundColor Green
