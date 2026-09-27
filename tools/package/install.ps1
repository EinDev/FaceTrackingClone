# Installs the FaceTrackingClone VRCFaceTracking module and downloads its model.
$ErrorActionPreference = "Stop"

$moduleId = "b3f1c0d2-5a44-4e18-9c77-2f8ad1e6b901"
$source = Join-Path $PSScriptRoot "module"
$target = Join-Path $env:APPDATA "VRCFaceTracking\CustomLibs\$moduleId"
$modelUrl = "https://raw.githubusercontent.com/Project-Babble/ProjectBabble/main/BabbleApp/Models/EFFB0E11BS128V7.5/onnx/model.onnx"

# VRCFT holds the module DLL open while running, so copying over it silently fails.
$running = Get-Process -Name "VRCFaceTracking", "VRCFaceTracking.ModuleProcess" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "VRCFaceTracking is running. Close it and run this again." -ForegroundColor Yellow
    exit 1
}

New-Item -ItemType Directory -Force $target | Out-Null
Get-ChildItem $source -File | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $target $_.Name) -Force
    Unblock-File (Join-Path $target $_.Name)
}

# The model belongs to Project Babble and is not redistributed here; fetch it from them.
$models = Join-Path $target "models"
$model = Join-Path $models "babble.onnx"
if (-not (Test-Path $model)) {
    New-Item -ItemType Directory -Force $models | Out-Null
    Write-Host "Downloading model from Project Babble (~23 MB)..."
    Invoke-WebRequest -Uri $modelUrl -OutFile $model -TimeoutSec 300 -UseBasicParsing
}

Write-Host ""
Write-Host "Installed to $target" -ForegroundColor Green
Write-Host "Start VRCFaceTracking; the module appears as 'VIVE Facial Tracker (FaceTrackingClone)'."
