<p align="center">
  <img src="docs/assets/logo.png" alt="HDRSnip" width="112" height="112" />
</p>

<h1 align="center">HDRSnip</h1>

<p align="center">
  <strong>Screenshots that stay sharp when Windows HDR is on.</strong><br />
  A tray-resident snipping tool that captures in FP16 scRGB and tone-maps properly.
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT" src="https://img.shields.io/badge/license-MIT-4CC2FF?style=flat-square" /></a>
  <img alt="Windows 10 1809+" src="https://img.shields.io/badge/Windows-10%201809%2B-A855F7?style=flat-square" />
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-4CC2FF?style=flat-square" />
</p>

---

## The problem

With HDR enabled, the built-in Snipping Tool and Print Screen read an 8-bit SDR view
of a floating-point HDR framebuffer. The result is washed out, blown out, or both —
text loses contrast and colours shift.

HDRSnip duplicates the desktop in `R16G16B16A16_FLOAT`, keeps the full float range
through cropping, and applies a real HDR→SDR transfer at the end. What you paste
looks like what you saw.

## Features

- **Region snip** over a frozen, HDR-correct preview of the monitor — what you drag is exactly what you get
- **Window snip** — in the same overlay, click a window instead of dragging
- **Full-screen snip** of the monitor under the cursor
- **Delayed snip** (3, 5 or 10 s) from the tray, for menus and hover states
- **Three tone-mapping curves** — Windows/OBS (default, best for UI and text), ACES filmic, Reinhard
- **SDR white level read from Windows** per monitor, with a manual override
- **Global hotkeys**, recorded in-app — no config file editing
- Copies to the clipboard as both DIB and lossless PNG
- Toast notification, click to open the editor; or open the editor immediately
- **Markup editor** — pen, highlighter, line, arrow, rectangle, ellipse, text, numbered steps, pixelate and crop, with undo/redo; marks stay editable until you copy or save
- Editor with fit/actual-size zoom, copy, save, and save-as
- Follows the Windows light/dark theme, live
- Multi-monitor and per-monitor-DPI aware
- GDI fallback where desktop duplication is unavailable (some VMs and remote sessions)

## Install

**From source, into the Start menu:**

```powershell
.\build.ps1 install
```

Installs to `%LOCALAPPDATA%\Programs\HDRSnip` with a Start menu shortcut. No admin needed.
Remove it again with `.\build.ps1 uninstall`.

**Portable single file:**

```powershell
.\build.ps1 portable
```

Requirements: Windows 10 1809 or later. Building needs the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); the published app is
self-contained and needs no runtime install.

## Default hotkeys

| Action | Hotkey |
|---|---|
| Region snip | `Ctrl + Shift + S` |
| Full screen | `Ctrl + Shift + Print Screen` |

Change them in **Settings → Hotkeys**: click a hotkey, press the combination you want.

> `Win + Shift + S` is reserved by Windows for the built-in Snipping Tool and cannot be
> claimed by any other app. To replace Print Screen system-wide, remap it under
> **Settings → Accessibility → Keyboard**.

## Markup

The editor opens on every capture (or from the toast). Pick a tool, draw, then **Copy** or
**Save** — the exported image is flattened at 1:1 with the capture, so nothing is resampled.

| Tool | Key | Notes |
|---|---|---|
| Select | `V` | Click a mark to move it, restyle it from the colour chip, or press `Delete`. Double-click text to edit it |
| Pen · Highlighter | `P` · `H` | Hold `Shift` for a straight line |
| Line · Arrow | `L` · `A` | Hold `Shift` to snap to 45° |
| Rectangle · Ellipse | `R` · `E` | Hold `Shift` for a square or circle |
| Text | `T` | Click to place. `Enter` adds a line, `Ctrl+Enter` or clicking away finishes, `Esc` cancels |
| Numbered step | `N` | Each click places the next number |
| Pixelate | `X` | Drag over anything that should not be readable |
| Crop | `C` | Drag the handles or draw a new area, then `Enter` or **Apply crop**. Non-destructive: crop again to widen |
| Colour & size | `S` | Remembered per tool |
| Undo · Redo | `Ctrl+Z` · `Ctrl+Y` | Every mark, move, restyle and crop is one step |

Closing with unexported markup asks first. If a new capture arrives while the editor holds
unexported markup, it opens in a second window rather than replacing your work.

To mark up an existing file: `HDRSnip.exe --edit image.png`.

## Settings

| Setting | Notes |
|---|---|
| SDR white level | Read from each monitor's Windows *SDR content brightness* by default. Turn that off to set a fixed level: higher = darker output |
| Tone-mapping curve | Windows/OBS for UI and text, ACES for games and video |
| Copy to clipboard | On by default |
| Auto-save PNG | Also writes to the save folder on every capture |
| Open editor immediately | Skips the toast |
| Start with Windows | Store package: Windows startup task. Unpackaged: per-user `Run` key. No elevation |

Settings live in `%LOCALAPPDATA%\HDRSnip\config.json`; errors, if any, in `errors.log`
beside it.

## How it works

```
hotkey ─▶ tray host ─▶ capture daemon (separate process)
                            │  DXGI Desktop Duplication, R16G16B16A16_FLOAT
                            │  warm D3D device + duplication + staging texture
                            ▼
                       shared memory  ──▶  tray host
                                             │  crop in half-float
                                             │  tone map via 64 KB LUT
                                             ▼
                                       clipboard · toast · editor
```

Three decisions carry most of the design:

**DXGI runs in its own process.** Desktop duplication can raise access violations that
no managed handler can catch. Isolating it means a driver fault costs one restarted
child, not the tray app. The daemon is started at launch and kept warm, so the first
snip is as fast as the tenth — and if a machine simply cannot do duplication, the
client stops retrying and falls through to GDI instead of paying a process launch every
time.

**Pixels never travel down the pipe.** A 4K half-float frame is 66 MB. The pipe carries
UTF-8 command lines only; the frame is published into a named shared-memory block and
mapped by the tray process, so it is copied once rather than serialised, streamed and
rebuilt.

**Tone mapping is a lookup table.** Every supported curve is a pure per-channel function
of the input sample, and every input is one of 65,536 half-float bit patterns. So each
curve collapses into a 64 KB byte table built once per capture, and the pixel loop is
three table reads per pixel — no `pow`, no per-pixel delegate. Measured on a 3840×2160
HDR frame: **9.4 ms** including bitmap allocation, against **69.1 ms** for the
straightforward float-expansion-plus-`MathF.Pow` version, at half the peak memory.

## Project layout

```
HDRSnip/
  Capture/     Daemon, DXGI session, shared-memory transport, tone mapper
  Views/       Tray host, mode bar, selection overlay, editor, settings
  Editing/     Annotation model, undo history and the markup canvas
  Controls/    HotkeyBox
  Services/    Hotkeys, autostart, notifications, theme
  Interop/     Every P/Invoke, in one file
  Models/      Config and hotkey types
  Theme/       Dark.xaml + Light.xaml palettes, Controls.xaml component library
packaging/     MSIX manifest and generated tiles
tools/         Generate-Assets.ps1 — the entire brand, from one vector definition
build.ps1      Every build, run, package and install task
```

## Building

```powershell
.\build.ps1 build       # debug build
.\build.ps1 run         # build and launch
.\build.ps1 assets      # regenerate logo, icon and every Store tile
.\build.ps1 package     # self-contained MSIX for the Store
.\build.ps1 clean
```

See [docs/BUILD.md](docs/BUILD.md) for the full task reference and
[docs/STORE-SUBMISSION.md](docs/STORE-SUBMISSION.md) for publishing.

## Privacy

HDRSnip makes no network connections and collects nothing. Screenshots, settings and the
error log never leave your machine. See [PRIVACY.md](PRIVACY.md).

## License

MIT — see [LICENSE](LICENSE).

The tone-mapping approach follows the same reasoning as OBS and other community HDR
capture tools; the implementation is original C# over [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows).
