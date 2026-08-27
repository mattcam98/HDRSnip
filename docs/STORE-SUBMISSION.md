# Microsoft Store submission

**Store ID** `9N101514P15J`
**Identity Name** `HDRSnipOpenSource.HDRSnip`
**Publisher** `CN=9F23CC6F-12A3-4502-9862-2F838EDA17E4`
**Publisher display name** `HDRSnip Open Source`

These already match `packaging/Package.appxmanifest`. If Partner Center ever reports an
identity mismatch, copy the values from **Product management → Product identity** —
they must match character for character.

---

## 1. Build the package

```powershell
.\build.ps1 package
```

Produces `artifacts/HDRSnip_<version>_x64.msix`, self-contained, with `resources.pri` so
the tiles scale correctly. Microsoft re-signs Store packages, so no code-signing
certificate is needed for this channel.

To bump the version, edit `<Version>` in `Directory.Build.props` — see
[BUILD.md](BUILD.md).

## 2. Submission sections

### Packages
Upload the `.msix`. Architecture is x64. Arm64 is a recommendation, not a requirement,
and can be added later.

### Properties
- **Category:** Productivity
- **System requirements:** Windows 10 version 1809 or later
- **Privacy policy URL:** optional. HDRSnip stores settings locally and has no accounts,
  no network calls and no telemetry.

### Age ratings
Run the questionnaire. A screen-capture utility with no user-generated content or
communication typically lands at the lowest rating.

### Store listing

**Product name:** HDRSnip

**Short title:** HDRSnip · **Voice title:** H D R Snip

**Short description**
> HDR-aware snipping tool for Windows. Take screenshots that look right when HDR is on — no more washed-out text.

**Description**
```
HDRSnip is an open-source snipping tool built for Windows HDR displays.

With HDR enabled, the built-in Snipping Tool and Print Screen read an 8-bit view of a
floating-point HDR framebuffer, so screenshots come out washed out or overexposed.
HDRSnip captures the desktop in high dynamic range (FP16 scRGB via DXGI Desktop
Duplication) and tone-maps it properly, so text and colour stay readable when you paste
or share.

• Region snip over a frozen, HDR-correct preview — what you drag is what you get
• Full-screen capture of the monitor under the cursor
• Three tone-mapping curves: Windows/OBS, ACES filmic, Reinhard
• Adjustable SDR white level to match your display
• Global hotkeys you can record in the app
• Copies to the clipboard automatically, as DIB and lossless PNG
• Click the notification to open the editor, zoom and save
• Lives in the notification area and uses no CPU while idle
• Follows your Windows light or dark theme
• Multi-monitor and per-monitor-DPI aware

Free and open source under the MIT licence:
https://github.com/mattcam98/HDRSnip
```

**Product features** (one per field)
```
HDR-correct screenshots when Windows HDR is on
FP16 scRGB capture via DXGI Desktop Duplication
Region snip over a frozen HDR-correct preview
Three tone-mapping curves with adjustable SDR white
Copies to the clipboard as DIB and lossless PNG
Recordable global hotkeys
Built-in editor with zoom, copy and save
Follows the Windows light and dark theme
Open source under the MIT licence
```

**Search terms** (max 7)
```
screenshot
snipping tool
HDR
screen capture
clipboard
tone mapping
productivity
```

**Copyright:** © 2026 HDRSnip contributors. MIT licence.
**Developed by:** HDRSnip Open Source

### Screenshots

Take these with HDRSnip itself, on an HDR display, at 1920×1080 or larger (1366×768 is
the minimum Partner Center accepts). Four that tell the story:

1. The selection overlay mid-drag, with the dimension badge visible
2. A side-by-side of the same HDR window captured by Snipping Tool and by HDRSnip —
   this is the single most persuasive image for this app
3. The editor showing a capture with the "HDR tone-mapped" badge
4. The settings window

### Store logos

`build.ps1 assets` writes `packaging/Images/StoreLogo300.png` for the 300×300 field.
For poster, box and hero art, composite `docs/assets/logo.png` on a solid background —
the Store rejects transparency in those slots.

### Pricing
Free.

---

## 3. The runFullTrust warning

Every Win32 desktop app packaged as MSIX must declare `runFullTrust`, and Partner Center
flags it. It is not a blocker; justify it in **Submission options → Notes for
certification**:

```
HDRSnip is a classic Win32/WPF desktop application packaged with MSIX (Desktop Bridge).
runFullTrust is required to:
  - capture the desktop via DXGI Desktop Duplication, which is the only API that
    exposes the FP16 HDR framebuffer this app exists to read
  - register global hotkeys and run from the notification area
  - write to the clipboard and to the user's chosen Pictures folder
The app does not request elevation, makes no network calls, and collects no data.
```

---

## 4. After approval

- Updates: bump `<Version>` in `Directory.Build.props`, run `.\build.ps1 package`, and
  create a new submission with the new `.msix`.
- Certification usually takes a few hours to a couple of days. The common failures are
  identity mismatch, missing screenshots and an unjustified restricted capability — all
  covered above.

## Sideloading for testers

```powershell
Add-AppxPackage -Path .\artifacts\HDRSnip_1.1.0.0_x64.msix
```

Unsigned packages need developer mode or a trusted certificate. Store installs do not.

## GitHub Releases

`.\build.ps1 portable` produces a self-contained single `HDRSnip.exe` suitable for a
GitHub release, alongside the Store build. Note in the release that the Store channel
auto-updates and the portable one does not.
