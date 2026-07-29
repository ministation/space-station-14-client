param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "artifacts",
    [string]$ApkName = "RobustAndroidPort-0.1.0-hub-beta.apk"
)

$ErrorActionPreference = "Stop"

# Paths
$ProjectRoot = $PSScriptRoot
$ProjFile = Join-Path $ProjectRoot "probes\Probe.AndroidHost\Probe.AndroidHost.csproj"
$SolutionFile = Join-Path $ProjectRoot "Robust.AndroidPort.sln"
$OutPath = Join-Path $ProjectRoot $OutputDir
$LogPath = Join-Path $OutPath "build.log"

# Create output directory
if (!(Test-Path $OutPath)) {
    New-Item -ItemType Directory -Force -Path $OutPath | Out-Null
}

Write-Host "=== 1. Initialize submodules ===" -ForegroundColor Cyan
try {
    & git submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) { throw "Git submodule failed" }
} catch {
    Write-Error "Failed to update submodules. Ensure git is installed and in PATH."
    exit 1
}

Write-Host "=== 2. Restore dependencies (dotnet restore) ===" -ForegroundColor Cyan
try {
    & dotnet restore $SolutionFile --nologo
    if ($LASTEXITCODE -ne 0) { throw "Dotnet restore failed" }
} catch {
    Write-Error "Failed to restore dependencies."
    exit 1
}

Write-Host "=== 3. Building project ($Configuration) ===" -ForegroundColor Cyan
$buildArgs = @(
    "build", $ProjFile,
    "-c", $Configuration,
    "-f", "net10.0-android",
    "-p:AndroidPackageFormat=apk",
    "--nologo",
    "/v:m"
)

$buildOutput = & dotnet @buildArgs 2>&1 | Tee-Object -FilePath $LogPath

if ($buildOutput -match "error CS|Build FAILED") {
    Write-Host "=== BUILD ERROR ===" -ForegroundColor Red
    $buildOutput | Select-String "error |Build FAILED" | ForEach-Object { Write-Host $_ }
    Write-Host "Full log saved to: $LogPath"
    exit 1
}

Write-Host "=== 4. Find and copy APK ===" -ForegroundColor Cyan

$BinDir = Join-Path $ProjectRoot "probes\Probe.AndroidHost\bin"
$SignedApk = Get-ChildItem -Path $BinDir -Recurse -Filter "*-Signed.apk" | 
             Sort-Object LastWriteTime -Descending | 
             Select-Object -First 1

if ($null -eq $SignedApk) {
    Write-Warning "Signed APK not found. Looking for any APK..."
    $AnyApk = Get-ChildItem -Path $BinDir -Recurse -Filter "*.apk" | 
              Sort-Object LastWriteTime -Descending | 
              Select-Object -First 1
    
    if ($null -eq $AnyApk) {
        Write-Error "APK file not found in bin folder after successful build."
        exit 1
    }
    $SignedApk = $AnyApk
}

$DestApk = Join-Path $OutPath $ApkName
Copy-Item $SignedApk.FullName $DestApk -Force

Write-Host "=== SUCCESS ===" -ForegroundColor Green
Write-Host "APK created: $DestApk"
Get-Item $DestApk | Format-List FullName, Length, LastWriteTime