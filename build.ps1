#Requires -Version 5.1
<#
.SYNOPSIS
  Single entry point for building, running, packaging and installing HDRSnip.

.DESCRIPTION
  Every task the project needs, in one place. The version comes from Directory.Build.props
  so the assembly, the MSIX manifest and the installed app can never disagree.

  Tasks:
    assets     Regenerate the logo, icon and every Store image from tools/Generate-Assets.ps1
    build      Debug build
    run        Build and launch the tray app
    publish    Framework-dependent release       -> artifacts/publish
    portable   Self-contained single-file release -> artifacts/portable
    package    Self-contained layout + MSIX      -> artifacts/HDRSnip_<version>_x64.msix
    install    Install to %LOCALAPPDATA%\Programs\HDRSnip with a Start menu shortcut
    uninstall  Remove the local install, shortcuts and autostart entry
    clean      Delete bin/, obj/ and artifacts/
    version    Print the current version

.EXAMPLE
  .\build.ps1 package

.EXAMPLE
  .\build.ps1 install -NoDesktop
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('assets', 'build', 'run', 'publish', 'portable', 'package', 'install', 'uninstall', 'clean', 'version')]
    [string]$Task = 'build',

    [string]$Version,
    [switch]$NoDesktop,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$Root      = $PSScriptRoot
$Project   = Join-Path $Root 'HDRSnip\HDRSnip.csproj'
$Packaging = Join-Path $Root 'packaging'
$Artifacts = Join-Path $Root 'artifacts'
$AppName   = 'HDRSnip'

# dotnet / winget may live on the machine PATH only.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path', 'User')

function Write-Step($Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note($Message) { Write-Host "    $Message" -ForegroundColor DarkGray }

function Get-ProductVersion {
    $props = Join-Path $Root 'Directory.Build.props'
    $xml = [xml](Get-Content $props -Raw)
    $node = $xml.SelectSingleNode('//Version')
    if (-not $node) { throw 'No <Version> element in Directory.Build.props' }
    return $node.InnerText.Trim()
}

# MSIX identity requires a four-part version.
function Get-PackageVersion([string]$Semver) {
    $parts = @($Semver.Split('.'))
    while ($parts.Count -lt 3) { $parts += '0' }
    return '{0}.{1}.{2}.0' -f $parts[0], $parts[1], $parts[2]
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed (exit $LASTEXITCODE)" }
}

function Find-SdkTool([string]$ToolName) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $kits)) { return $null }
    $candidates = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending
    foreach ($dir in $candidates) {
        $exe = Join-Path $dir.FullName "x64\$ToolName"
        if (Test-Path $exe) { return $exe }
    }
    return $null
}

function New-Shortcut([string]$Path, [string]$Target, [string]$WorkDir) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.WorkingDirectory = $WorkDir
    $link.IconLocation = $Target
    $link.Description = 'HDR-aware snipping tool for Windows'
    $link.Save()
}

if (-not $Version) { $Version = Get-ProductVersion }
$PackageVersion = Get-PackageVersion $Version

$InstallRoot  = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$StartMenuLnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
$DesktopLnk   = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"

switch ($Task) {

    'version' {
        Write-Host "$AppName $Version (package $PackageVersion)"
    }

    'assets' {
        Write-Step 'Generating brand assets'
        & (Join-Path $Root 'tools\Generate-Assets.ps1')
    }

    'build' {
        Write-Step 'Building (Debug)'
        Invoke-Dotnet @('build', $Project, '-c', 'Debug')
    }

    'run' {
        Write-Step 'Running'
        Invoke-Dotnet @('run', '--project', $Project, '-c', 'Debug')
    }

    'publish' {
        $out = Join-Path $Artifacts 'publish'
        Write-Step "Publishing framework-dependent -> $out"
        Invoke-Dotnet @('publish', $Project, '-c', 'Release', '-r', 'win-x64',
                        '--self-contained', 'false', '-o', $out)
        Write-Note "$out\HDRSnip.exe  (requires the .NET 8 Desktop Runtime)"
    }

    'portable' {
        $out = Join-Path $Artifacts 'portable'
        Write-Step "Publishing self-contained single file -> $out"
        Invoke-Dotnet @('publish', $Project, '-c', 'Release', '-r', 'win-x64',
                        '--self-contained', 'true',
                        '-p:PublishSingleFile=true',
                        '-p:IncludeNativeLibrariesForSelfExtract=true',
                        '-p:DebugType=none',
                        '-o', $out)
        Write-Note "$out\HDRSnip.exe  (no runtime install required)"
    }

    'package' {
        $makeappx = Find-SdkTool 'makeappx.exe'
        if (-not $makeappx) {
            Write-Step 'MakeAppx not found - installing the Windows SDK (several minutes)'
            & winget install Microsoft.WindowsSDK.10.0.22621 --accept-package-agreements --accept-source-agreements
            $makeappx = Find-SdkTool 'makeappx.exe'
            if (-not $makeappx) {
                throw 'MakeAppx still unavailable. Install the Windows SDK from https://developer.microsoft.com/windows/downloads/windows-sdk/ and re-run.'
            }
        }

        $stage   = Join-Path $Artifacts 'msix-layout'
        $payload = Join-Path $stage 'HDRSnip'
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $payload, (Join-Path $stage 'Images'), $Artifacts | Out-Null

        Write-Step "Publishing self-contained x64 payload (v$PackageVersion)"
        Invoke-Dotnet @('publish', $Project, '-c', 'Release', '-r', 'win-x64',
                        '--self-contained', 'true',
                        '-p:PublishSingleFile=false',
                        '-p:DebugType=none',
                        '-o', $payload)

        Write-Step 'Staging manifest and images'
        Copy-Item (Join-Path $Packaging 'Images\*') (Join-Path $stage 'Images') -Force
        # Stamp only the Identity version - never MinVersion / MaxVersionTested.
        $manifest = Get-Content (Join-Path $Packaging 'Package.appxmanifest') -Raw
        $manifest = [regex]::Replace($manifest, '(<Identity\b[^>]*\bVersion=")[^"]+(")', "`${1}$PackageVersion`${2}")
        Set-Content -Path (Join-Path $stage 'AppxManifest.xml') -Value $manifest -Encoding UTF8

        # Without resources.pri, Windows ignores the .scale-* and .targetsize-*
        # images and stretches the single base tile everywhere.
        $makepri = Find-SdkTool 'makepri.exe'
        if ($makepri) {
            Write-Step 'Indexing tile resources (MakePri)'
            $priConfig = Join-Path $Artifacts 'priconfig.xml'
            & $makepri createconfig /cf $priConfig /dq en-US /o | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "MakePri createconfig failed (exit $LASTEXITCODE)" }
            & $makepri new /pr $stage /cf $priConfig /of (Join-Path $stage 'resources.pri') /o | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "MakePri new failed (exit $LASTEXITCODE)" }
            Remove-Item $priConfig -Force -ErrorAction SilentlyContinue
        } else {
            Write-Note 'MakePri not found - packing without resources.pri; tiles will not scale per DPI.'
        }

        $msix = Join-Path $Artifacts "HDRSnip_${PackageVersion}_x64.msix"
        if (Test-Path $msix) { Remove-Item $msix -Force }

        Write-Step 'Packing MSIX'
        & $makeappx pack /d $stage /p $msix /o
        if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed (exit $LASTEXITCODE)" }

        Write-Host ''
        Write-Host "Created $msix" -ForegroundColor Green
        Write-Note 'Partner Center -> Start submission -> Packages -> upload this .msix'
        Write-Note 'Microsoft re-signs Store packages; no local certificate is needed.'
    }

    'install' {
        Get-Process $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 300

        if (-not $SkipBuild) {
            Write-Step "Publishing self-contained release -> $InstallRoot"
            New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
            Invoke-Dotnet @('publish', $Project, '-c', 'Release', '-r', 'win-x64',
                            '--self-contained', 'true',
                            '-p:PublishSingleFile=false',
                            '-p:DebugType=none',
                            '-o', $InstallRoot)
        }

        $exe = Join-Path $InstallRoot "$AppName.exe"
        if (-not (Test-Path $exe)) { throw "$AppName.exe not found at $exe" }

        New-Shortcut -Path $StartMenuLnk -Target $exe -WorkDir $InstallRoot
        Write-Note "Start menu: $StartMenuLnk"
        if (-not $NoDesktop) {
            New-Shortcut -Path $DesktopLnk -Target $exe -WorkDir $InstallRoot
            Write-Note "Desktop:    $DesktopLnk"
        }

        Write-Host ''
        Write-Host "Installed to $InstallRoot" -ForegroundColor Green
        Write-Note "Search the Start menu for '$AppName' to launch."
    }

    'uninstall' {
        Write-Step "Removing $AppName"
        Get-Process $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 300
        foreach ($path in @($InstallRoot, $StartMenuLnk, $DesktopLnk)) {
            if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        }
        Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
                            -Name $AppName -ErrorAction SilentlyContinue
        Write-Note 'Install, shortcuts and autostart entry removed.'
        Write-Note "Settings remain in %LOCALAPPDATA%\$AppName - delete that folder to reset."
    }

    'clean' {
        Write-Step 'Cleaning build output'
        $targets = @(
            (Join-Path $Root 'HDRSnip\bin'),
            (Join-Path $Root 'HDRSnip\obj'),
            (Join-Path $Root 'packaging\bin'),
            (Join-Path $Root 'packaging\obj'),
            $Artifacts,
            (Join-Path $Root 'publish')
        )
        foreach ($dir in $targets) {
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force; Write-Note "removed $dir" }
        }
    }
}
