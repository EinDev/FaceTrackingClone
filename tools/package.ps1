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

# ONNX Runtime's binaries ship in the zip, so its licence and third-party notices must too.
# The restore output says exactly which packages and versions were used, and where they live.
$assets = Get-Content (Join-Path $root "src\FaceTrackingClone.Core\obj\project.assets.json") -Raw | ConvertFrom-Json
$packageRoot = @($assets.packageFolders.PSObject.Properties.Name)[0]
foreach ($lib in $assets.libraries.PSObject.Properties) {
    if ($lib.Name -notlike "Microsoft.ML.OnnxRuntime*") { continue }
    $packageName = $lib.Name.Split("/")[0]
    $dest = Join-Path $stage "licenses\$packageName"
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($file in $lib.Value.files | Where-Object { $_ -match "^(LICENSE|ThirdPartyNotices)" }) {
        Copy-Item (Join-Path $packageRoot (Join-Path $lib.Value.path $file)) $dest
    }
}
if (-not (Test-Path (Join-Path $stage "licenses"))) { throw "ONNX Runtime licence files not found." }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Packaged $zip ($mb MB)" -ForegroundColor Green
