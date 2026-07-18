# HDRSnip

<p align="center">
  <img src="HDRSnip/Assets/logo.png" alt="HDRSnip logo" width="128" />
</p>

Open-source **HDR-aware snipping tool** for Windows 10/11. Captures the desktop in FP16 scRGB via DXGI Desktop Duplication, tone-maps to SDR (Windows/OBS-style), and mirrors the Snipping Tool workflow: floating mode bar, rectangular region select, clipboard + editor.

## Why

With HDR enabled, Snipping Tool / Print Screen often produce washed-out, overexposed screenshots because they read an 8-bit SDR view of an HDR framebuffer. HDRSnip keeps the full float range and applies proper HDR→SDR tone mapping so UI text stays readable.

## Features

- Rectangular snip (frozen HDR-correct preview overlay)
- Fullscreen / window (monitor under cursor)
- Floating mode toolbar (Snipping Tool–style)
- System tray app with global hotkeys
- Tone mapping: **Windows/OBS** (default), ACES, Reinhard
- Adjustable SDR white level (nits)
- Copy to clipboard, optional auto-save PNG, post-capture editor
- Multi-monitor aware
- SDR GDI fallback when FP16 duplication is unavailable

## Requirements

- Windows 10 1703+ / Windows 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or SDK to build)
- GPU/driver supporting DXGI Desktop Duplication

## Run from source

```powershell
cd HDRSnip
dotnet run
```

## Build release exe

```powershell
cd HDRSnip
dotnet publish -c Release -r win-x64 --self-contained false -o ..\publish
```

Self-contained single-file (no runtime install needed):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\publish
```

Run `..\publish\HDRSnip.exe`. The tray icon appears in the notification area.

## Hotkeys (defaults)

| Action | Hotkey |
|--------|--------|
| Rectangular snip | `Ctrl+Shift+S` |
| Fullscreen snip | `Ctrl+Shift+PrintScreen` |

Tray → **New snip** opens the mode toolbar. Change hotkeys in `%LOCALAPPDATA%\HDRSnip\config.json` (modifiers: Alt=1, Ctrl=2, Shift=4, Win=8).

> `Win+Shift+S` is reserved by Windows Snipping Tool and usually cannot be stolen. Remap Print Screen in **Settings → Accessibility → Keyboard** if you want a system-wide replacement.

## Settings

Tray → **Settings…**

| Setting | Notes |
|---------|--------|
| SDR white (nits) | Higher = darker output. Start at **250**; try **200–280** if still bright |
| Tone mapping | Prefer **Windows / OBS** for UI/text screenshots |
| Copy / toast / Editor | Copied by default; toast opens editor when clicked |
| Open editor immediately | Opt-in to skip the toast and open the editor |
| Start with Windows | HKCU Run key |

## How it works

1. Hotkey / tray starts a capture.
2. For region snips: grab the monitor under the cursor in `DXGI_FORMAT_R16G16B16A16_FLOAT`, tone-map a preview, show a dimmed overlay, crop the float buffer to the selection.
3. Apply tone map (divide by `sdrWhiteNits / 80`, clip, sRGB encode — same idea as OBS).
4. Copy PNG-ready bitmap to the clipboard and optionally open the editor.

## Project layout

```
HDRSnip/
  Capture/     DXGI FP16 grab + tone mapping
  Views/       Overlay, toolbar, editor, settings
  Services/    Hotkeys, autostart
  Models/      Config
```

## License

MIT — see [LICENSE](LICENSE).

Tone-mapping approach inspired by OBS and community HDR screenshot tools; implementation is original C# / Vortice code.
