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

`build.ps1 assets` writes ready-to-upload listing art to `packaging/Listing/`, composited
on the app's dark surface because Partner Center rejects transparency in the poster, box
and hero slots:

| File | Field |
|---|---|
| `poster-9x16-720x1080.png` / `-1440x2160.png` | 9:16 Poster art |
| `boxart-1x1-1080.png` / `-2160.png` | 1:1 Box art |
| `superhero-16x9-1920x1080.png` / `-3840x2160.png` | 16:9 Super hero art — carries no product title, as that slot requires |
| `tile-300.png`, `tile-150.png`, `tile-71.png` | Store display images |

The transparent tiles in `packaging/Images/` are for the package itself, where Windows
tints them. Do not upload those to the listing fields.

### Pricing
Free.

---

## 3. The runFullTrust capability

Partner Center flags `runFullTrust` as a restricted capability and asks you to justify it
before the package is accepted. This is expected, not a rejection.

Every Win32 desktop application packaged as MSIX declares `runFullTrust` — it is required
by the Desktop Bridge app model itself, for any app with
`EntryPoint="Windows.FullTrustApplication"`. Do not remove it; the package will not run.
Approval is routine for desktop apps, though the first submission can sit for a few days.

**The field caps at 500 characters.** Paste this into
**Restricted capabilities → Why do you need the runFullTrust capability** (497 chars):

```
HDRSnip is a Win32/WPF desktop application packaged as MSIX via the Desktop Bridge. runFullTrust is mandatory for the EntryPoint="Windows.FullTrustApplication" model; the app cannot run without it.

It is used only to: capture the desktop in HDR via DXGI Desktop Duplication, register global capture hotkeys, write to the clipboard, and save PNGs to a folder the user chooses.

No elevation (asInvoker), no network access, no telemetry, no data collection. MIT source: github.com/mattcam98/HDRSnip
```

If a reviewer asks about the second HDRSnip process they can see running:

> HDRSnip runs one child copy of its own executable to host DXGI Desktop Duplication,
> so a graphics driver fault cannot terminate the tray app. It talks only to its parent
> over a local named pipe and shared memory, and does nothing else.

<details>
<summary>Long-form version, for any field that allows it</summary>


```
HDRSnip is a classic Win32 desktop application (C#/WPF, .NET 8) packaged with
MSIX via the Desktop Bridge. It is declared as
EntryPoint="Windows.FullTrustApplication", and runFullTrust is required by that
app model itself — a packaged desktop application cannot run without it.

The app takes HDR-correct screenshots. When Windows HDR is enabled, the built-in
Snipping Tool and Print Screen produce washed-out results; HDRSnip captures the
desktop in floating-point colour and tone-maps it to SDR so text and colour stay
readable.

Full trust is used for exactly four things:

1. DXGI Desktop Duplication (IDXGIOutput5::DuplicateOutput1 with
   DXGI_FORMAT_R16G16B16A16_FLOAT) to read the desktop in high dynamic range.
   This is the core function of the app.
2. RegisterHotKey for the user-configurable global capture hotkeys
   (default Ctrl+Shift+S and Ctrl+Shift+PrintScreen).
3. Clipboard access, to place the finished screenshot on the clipboard.
4. Writing PNG files to a folder the user chooses (default: Pictures\HDRSnip),
   and a small JSON settings file plus an error log under LocalAppData.

The app also runs a second instance of its own executable as a child process to
host the DXGI capture work, so that a graphics driver fault cannot terminate the
tray application. This child communicates only with its parent, over a local
named pipe and shared memory. It performs no other function.

HDRSnip does not request administrator elevation (the manifest declares
asInvoker), makes no network connections, has no accounts or telemetry, and
collects no personal data. Screenshots never leave the user's machine. Desktop
Duplication respects protected-content restrictions enforced by Windows.

The app is open source under the MIT licence and the full implementation can be
reviewed at https://github.com/mattcam98/HDRSnip
```

</details>

The justification rests on the app-model requirement, which is not arguable. It
deliberately avoids claiming Desktop Duplication is the *only* way to read an HDR
framebuffer — `Windows.Graphics.Capture` also supports `R16G16B16A16Float` on Windows 11,
and a reviewer who knows that should not find an overstatement in your submission.

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
