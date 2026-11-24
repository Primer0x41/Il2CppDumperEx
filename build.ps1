# Il2CppDumperEx Automated Build & Publish Script
# Produces a clean, standalone single-file Il2CppDumperEx.exe (zero clutter, zero %TEMP% extraction)

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Framework = "",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Framework)) {
    $Framework = if ($Runtime -like "win*") { "net11.0-windows" } else { "net11.0" }
}

$RootDir = $PSScriptRoot
$PublishDir = Join-Path $RootDir "publish"
$exeName = if ($Runtime -like "win*") { "Il2CppDumperEx.exe" } else { "Il2CppDumperEx" }
$ExePath = Join-Path $PublishDir $exeName

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "               Il2CppDumperEx Build               " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Run Unit Tests
if (-not $SkipTests) {
    Write-Host "[1/2] Running test suite..." -ForegroundColor Yellow
    dotnet test "$RootDir\Il2CppDumperEx.sln" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Test suite failed. Packaging aborted."
        exit $LASTEXITCODE
    }
} else {
    Write-Host "[1/2] Skipping tests (-SkipTests specified)..." -ForegroundColor DarkGray
}

# 2. Publish Single-File Executable
Write-Host "[2/2] Publishing clean single-file executable ($Runtime, $Framework)..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    # Remove previous executable and archive outputs without deleting user subdirectories
    if (Test-Path $ExePath) { Remove-Item $ExePath -Force }
    Get-ChildItem -Path $PublishDir -Filter "*.zip" -File | Remove-Item -Force
    Get-ChildItem -Path $PublishDir -Filter "*.tar.gz" -File | Remove-Item -Force
    Get-ChildItem -Path $PublishDir -Filter "SHA256SUMS.txt" -File | Remove-Item -Force
} else {
    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
}

dotnet publish "$RootDir\src\Il2CppDumperEx\Il2CppDumperEx.csproj" `
    -c $Configuration `
    -f $Framework `
    -r $Runtime `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $PublishDir `
    --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed."
    exit $LASTEXITCODE
}

# Clean any lingering pdb or symbol artifacts if any
Get-ChildItem -Path $PublishDir -Filter "*.pdb" -Recurse | Remove-Item -Force

$exeSize = if (Test-Path $ExePath) { (Get-Item $ExePath).Length } else { 0 }

Write-Host "==================================================" -ForegroundColor Green
Write-Host "                BUILD SUCCESSFUL                  " -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
Write-Host "Standalone Executable: $ExePath ($([Math]::Round($exeSize / 1KB, 2)) KB)"