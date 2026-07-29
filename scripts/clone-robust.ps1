param(
    [string]$RepoUrl = "https://github.com/space-wizards/RobustToolbox.git",
    [string]$Target = "vendor/RobustToolbox",
    [string]$Branch = "master"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (Test-Path $Target) {
    Write-Host "Already exists: $Target"
    Write-Host "Updating..."
    Push-Location $Target
    git fetch --depth 1 origin $Branch
    git checkout $Branch
    git pull --depth 1 origin $Branch
    Pop-Location
    exit 0
}

New-Item -ItemType Directory -Force -Path (Split-Path $Target) | Out-Null
Write-Host "Shallow cloning RobustToolbox (this can take a while)..."
git clone --depth 1 --branch $Branch $RepoUrl $Target
Write-Host "Initializing critical submodules..."
Push-Location $Target
git submodule update --init --depth 1 -- NetSerializer "Lidgren.Network/Lidgren.Network" Robust.LoaderApi
Pop-Location
Write-Host "Done. Next: fill docs/inventory.md"
