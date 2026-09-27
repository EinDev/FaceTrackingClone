# Downloads the lip blendshape model.
#
# The model is not committed: it is ~23MB and belongs to Project Babble
# (https://github.com/Project-Babble/ProjectBabble). Check their licence before redistributing.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "models"
$file = Join-Path $dest "babble.onnx"

$url = "https://raw.githubusercontent.com/Project-Babble/ProjectBabble/main/BabbleApp/Models/EFFB0E11BS128V7.5/onnx/model.onnx"

New-Item -ItemType Directory -Force $dest | Out-Null

if (Test-Path $file) {
    $mb = [math]::Round((Get-Item $file).Length / 1MB, 2)
    Write-Host "Model already present ($mb MB): $file"
    Write-Host "Delete it first if you want to re-download."
    exit 0
}

Write-Host "Downloading model..."
Invoke-WebRequest -Uri $url -OutFile $file -TimeoutSec 300

$mb = [math]::Round((Get-Item $file).Length / 1MB, 2)
Write-Host "Saved $file ($mb MB)"
Write-Host "Verify with: ftclone model"
