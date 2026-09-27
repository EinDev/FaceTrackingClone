# Builds and installs the VRCFaceTracking module.
#
# VRCFT loads custom modules from %APPDATA%\VRCFaceTracking\CustomLibs\<ModuleId>\.

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$VrcftPath = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\FaceTrackingClone.VrcftModule\FaceTrackingClone.VrcftModule.csproj"
$moduleId = "b3f1c0d2-5a44-4e18-9c77-2f8ad1e6b901"
$target = Join-Path $env:APPDATA "VRCFaceTracking\CustomLibs\$moduleId"

# VRCFT holds the module DLL open while running, so copying over it silently fails.
$running = Get-Process -Name "VRCFaceTracking", "VRCFaceTracking.ModuleProcess" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "VRCFaceTracking is running. Close it first:" -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host "  $($_.ProcessName) (pid $($_.Id))" }
    exit 1
}

if (-not (Test-Path (Join-Path $root "models\babble.onnx"))) {
    Write-Host "Model missing. Fetching..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "fetch-model.ps1")
}

$cli = Join-Path $root "src\FaceTrackingClone\FaceTrackingClone.csproj"

if (-not $SkipBuild) {
    Write-Host "Building module..."
    $buildArgs = @($project, "-c", $Configuration, "--nologo", "-v", "quiet")
    if ($VrcftPath) { $buildArgs += "-p:VrcftPath=$VrcftPath" }
    & dotnet build @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    # The viewer ships with the module so it can be auto-launched for a live preview.
    Write-Host "Building viewer..."
    & dotnet build $cli -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Viewer build failed." }
}

$out = Join-Path $root "src\FaceTrackingClone.VrcftModule\bin\$Configuration\net10.0-windows\win-x64"
if (-not (Test-Path $out)) { throw "Build output not found at $out" }

New-Item -ItemType Directory -Force $target | Out-Null

# Debug symbols and import libraries are build artefacts, not runtime dependencies.
$exclude = @("*.pdb", "*.lib")

Write-Host "Installing to $target"
Get-ChildItem $out -Recurse -File | Where-Object {
    $name = $_.Name
    -not ($exclude | Where-Object { $name -like $_ })
} | ForEach-Object {
    $rel = $_.FullName.Substring($out.Length + 1)
    $dest = Join-Path $target $rel
    New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
    Copy-Item $_.FullName $dest -Force
    Write-Host "  $rel"
}

# Ship the viewer next to the module. Only the few files it adds beyond what the module
# already provides (Core, ONNX Runtime) are copied.
$cliOut = Join-Path $root "src\FaceTrackingClone\bin\$Configuration\net10.0-windows\win-x64"
if (Test-Path $cliOut) {
    Write-Host "Installing viewer..."
    foreach ($name in 'ftclone.exe', 'ftclone.dll', 'ftclone.runtimeconfig.json', 'ftclone.deps.json') {
        $src = Join-Path $cliOut $name
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $target $name) -Force
            Write-Host "  $name"
        }
    }
}
else {
    Write-Host "Viewer output not found at $cliOut - auto-launch will be skipped." -ForegroundColor Yellow
}

$mb = [math]::Round((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host "Installed ($mb MB)." -ForegroundColor Green
Write-Host ""
Write-Host "Next:"
Write-Host "  1. Start VRCFaceTracking; the module appears as"
Write-Host "     'VIVE Facial Tracker (FaceTrackingClone)'."
Write-Host "  2. Keep the SRanipal module installed for EYE tracking only."
Write-Host "  3. Nothing else may hold the lip camera - only one process can own it."
