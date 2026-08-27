# Building and releasing HDRSnip

Everything runs through `build.ps1` at the repo root. There is no other script to know
about, and no task that needs Visual Studio.

## Prerequisites

| For | You need |
|---|---|
| Building and running | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| `package` (MSIX) | Windows SDK, for `makeappx.exe` and `makepri.exe`. `build.ps1 package` offers to install it via winget if it is missing |
| Everything else | Nothing extra |

## Tasks

```powershell
.\build.ps1 <task> [-Version x.y.z] [-NoDesktop] [-SkipBuild]
```

| Task | What it does | Output |
|---|---|---|
| `build` | Debug build | `HDRSnip/bin/Debug/…` |
| `run` | Build and launch the tray app | — |
| `assets` | Regenerate the logo, icon, favicons and every MSIX tile | `HDRSnip/Assets`, `packaging/Images`, `docs/assets` |
| `publish` | Framework-dependent release | `artifacts/publish` |
| `portable` | Self-contained single file | `artifacts/portable` |
| `package` | Self-contained payload + `resources.pri` + MSIX | `artifacts/HDRSnip_<version>_x64.msix` |
| `install` | Install to `%LOCALAPPDATA%\Programs\HDRSnip` with a Start menu shortcut | — |
| `uninstall` | Remove the install, shortcuts and autostart entry | — |
| `clean` | Delete `bin/`, `obj/` and `artifacts/` | — |
| `version` | Print the current version | — |

`-NoDesktop` skips the desktop shortcut on `install`. `-SkipBuild` reuses whatever is
already in the install directory.

## Versioning

`Directory.Build.props` holds the single `<Version>` for the whole product.

```xml
<Version>1.1.0</Version>
```

`build.ps1` reads it and stamps the four-part MSIX identity version at pack time, so the
assembly, the installer and the package can never disagree. To cut a release, bump that
one number.

`-Version` overrides it for a one-off package without editing the file.

## Brand assets

Every icon, tile and favicon in the repo is generated. `tools/Generate-Assets.ps1`
defines the mark once as vector geometry and renders it at every size Windows and the
Store ask for, including two optical variants — below 48 px the frame tightens and the
strokes thicken so the mark survives in the notification area.

Do not hand-edit the PNGs. Change the geometry or palette at the top of the script and
run `.\build.ps1 assets`.

Outputs:

| Path | Contents |
|---|---|
| `HDRSnip/Assets/logo.png` | 1024 px transparent master |
| `HDRSnip/Assets/app.ico` | 16–256 px, DIB below 128 px and PNG above |
| `packaging/Images/` | Every tile at scale 100/125/150/200/400 plus unplated target sizes |
| `docs/assets/` | `logo.svg`, `logo.png`, `favicon.png`, `favicon.ico` |
| `packaging/Listing/` | Store listing art: poster, box, hero and display tiles on a solid background |

`build.ps1 package` runs `makepri` over the staged layout, which is what makes Windows
actually pick the right scale variant. Without `resources.pri` the base tile is stretched
at every DPI.

## Release checklist

1. Bump `<Version>` in `Directory.Build.props`.
2. `.\build.ps1 assets` if the mark or palette changed.
3. `.\build.ps1 package`.
4. Sideload-test the package on a clean machine:
   `Add-AppxPackage -Path .\artifacts\HDRSnip_<version>_x64.msix`
   (needs a trusted certificate or developer mode; Store installs skip that).
5. Follow [STORE-SUBMISSION.md](STORE-SUBMISSION.md).

## Packaging without Visual Studio

There is no Windows Application Packaging (`.wapproj`) project. `build.ps1 package` drives
`makeappx` and `makepri` directly, so `dotnet build` on the solution works with nothing but
the .NET SDK installed, and the MSIX it produces is exactly what Partner Center expects.
Microsoft re-signs Store packages, so the Visual Studio signing wizard is not needed for
that channel.

For sideload testing on a machine without developer mode, sign the package yourself. Create
a certificate whose subject matches the manifest `Publisher` character for character:

```bash
New-SelfSignedCertificate -Type Custom -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature -Subject "CN=9F23CC6F-12A3-4502-9862-2F838EDA17E4" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
```

Then sign with its thumbprint:

```bash
signtool sign /fd SHA256 /sha1 <thumbprint> .\artifacts\HDRSnip_1.1.0.0_x64.msix
```

The certificate must also be trusted on the target machine (import it into
*Local Machine → Trusted People*).
