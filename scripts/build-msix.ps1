#Requires -Version 5.1
<#
.SYNOPSIS
  Build an MSIX package for Microsoft Store / sideload upload.

.DESCRIPTION
  Publishes HDRSnip (self-contained x64), stages the MSIX layout, and runs MakeAppx.
  Requires Windows SDK (MakeAppx.exe). Does not sign — Store re-signs on upload.
#>
param(
    [string]$Version = "1.0.0.0",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Project = Join-Path $RepoRoot "HDRSnip\HDRSnip.csproj"
$Packaging = Join-Path $RepoRoot "packaging"
$Stage = Join-Path $RepoRoot "artifacts\msix-layout"
$Publish = Join-Path $Stage "HDRSnip"
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot "artifacts" }

$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path","User")

function Find-MakeAppx {
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $kits)) { return $null }
    Get-ChildItem $kits -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object {
            $p = Join-Path $_.FullName "x64\makeappx.exe"
            if (Test-Path $p) { return $p }
        } |
        Select-Object -First 1
}

$makeappx = Find-MakeAppx
if (-not $makeappx) {
    Write-Host "MakeAppx not found. Installing Windows SDK (this can take several minutes)..."
    winget install Microsoft.WindowsSDK.10.0.22621 --accept-package-agreements --accept-source-agreements
    $makeappx = Find-MakeAppx
    if (-not $makeappx) {
        throw "MakeAppx still not found. Install 'Windows Software Development Kit' from https://developer.microsoft.com/windows/downloads/windows-sdk/ then re-run."
    }
}

Write-Host "Using MakeAppx: $makeappx"

# Update only the Identity Version attribute (do NOT touch MinVersion / MaxVersionTested)
$manifestSrc = Join-Path $Packaging "Package.appxmanifest"
$manifestText = Get-Content $manifestSrc -Raw
$manifestText = [regex]::Replace(
    $manifestText,
    '(<Identity\b[^>]*\bVersion=")[^"]+(")',
    "`${1}$Version`${2}")

if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Publish | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Stage "Images") | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "Publishing self-contained x64 app..."
& dotnet publish $Project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $Publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $Packaging "Images\*") (Join-Path $Stage "Images") -Force
Set-Content -Path (Join-Path $Stage "AppxManifest.xml") -Value $manifestText -Encoding UTF8

$msix = Join-Path $OutDir "HDRSnip_${Version}_x64.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }

Write-Host "Packing MSIX..."
& $makeappx pack /d $Stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed" }

Write-Host ""
Write-Host "Created: $msix"
Write-Host "Next: Partner Center → Start submission → Packages → upload this .msix"
Write-Host "(Microsoft will re-sign it; no local certificate required for Store.)"
