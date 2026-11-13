# Il2CppDumperEx Automated Build & Publish Script
# Produces a clean, standalone single-file Il2CppDumperEx.exe (zero clutter, zero %TEMP% extraction)

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$RootDir = $PSScriptRoot
$PublishDir = Join-Path $RootDir "publish"
$ExePath = Join-Path $PublishDir "Il2CppDumperEx.exe"

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "       Il2CppDumperEx Build & Packaging          " -ForegroundColor Cyan
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
Write-Host "[2/2] Publishing clean single-file executable ($Runtime)..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

dotnet publish "$RootDir\src\Il2CppDumperEx\Il2CppDumperEx.csproj" `
    -c $Configuration `
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

$exeSize = (Get-Item $ExePath).Length

Write-Host "==================================================" -ForegroundColor Green
Write-Host "                BUILD SUCCESSFUL                  " -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
Write-Host "Standalone Executable: $ExePath ($([Math]::Round($exeSize / 1KB, 2)) KB)"
Write-Host "Zero extra loose files, zero language folders, zero %TEMP% extraction."
