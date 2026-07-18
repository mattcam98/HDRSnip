#Requires -Version 5.1
<#
.SYNOPSIS
  Install HDRSnip locally so it appears in the Start menu (and optionally Desktop).

.DESCRIPTION
  Publishes a self-contained win-x64 build to %LOCALAPPDATA%\Programs\HDRSnip
  and creates Start Menu / Desktop shortcuts. No admin required.
#>
param(
    [switch]$Uninstall,
    [switch]$NoDesktop,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$AppName = "HDRSnip"
$InstallRoot = Join-Path $env:LOCALAPPDATA "Programs\HDRSnip"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Project = Join-Path $RepoRoot "HDRSnip\HDRSnip.csproj"
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$StartMenuLnk = Join-Path $StartMenuDir "$AppName.lnk"
$DesktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "$AppName.lnk"

function New-Shortcut([string]$Path, [string]$Target, [string]$WorkDir, [string]$Icon) {
    $w = New-Object -ComObject WScript.Shell
    $s = $w.CreateShortcut($Path)
    $s.TargetPath = $Target
    $s.WorkingDirectory = $WorkDir
    $s.IconLocation = $Icon
    $s.Description = "HDR-aware snipping tool for Windows"
    $s.Save()
}

if ($Uninstall) {
    Write-Host "Uninstalling $AppName..."
    Get-Process HDRSnip -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if (Test-Path $InstallRoot) { Remove-Item $InstallRoot -Recurse -Force }
    if (Test-Path $StartMenuLnk) { Remove-Item $StartMenuLnk -Force }
    if (Test-Path $DesktopLnk) { Remove-Item $DesktopLnk -Force }
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "HDRSnip" -ErrorAction SilentlyContinue
    Write-Host "Removed. Start menu shortcut cleared."
    exit 0
}

if (-not (Test-Path $Project)) {
    throw "Could not find project at $Project"
}

$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path","User")

if (-not $SkipBuild) {
    Write-Host "Publishing self-contained release..."
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    & dotnet publish $Project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -o $InstallRoot
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

$exe = Join-Path $InstallRoot "HDRSnip.exe"
if (-not (Test-Path $exe)) { throw "HDRSnip.exe not found at $exe" }

New-Shortcut -Path $StartMenuLnk -Target $exe -WorkDir $InstallRoot -Icon $exe
Write-Host "Start menu: $StartMenuLnk"

if (-not $NoDesktop) {
    New-Shortcut -Path $DesktopLnk -Target $exe -WorkDir $InstallRoot -Icon $exe
    Write-Host "Desktop:    $DesktopLnk"
}

Write-Host ""
Write-Host "Installed to $InstallRoot"
Write-Host "Open the Start menu and type 'HDRSnip' to launch."
Write-Host "Uninstall later with:  .\scripts\install-startmenu.ps1 -Uninstall"
