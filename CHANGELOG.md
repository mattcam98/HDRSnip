# Changelog

## Unreleased

### New

- **Window snip:** in the selection overlay, hover highlights the window under the cursor and a click captures it. Dragging still selects a region.
- **Delayed snip:** the tray mode bar has a delay control (3, 5 or 10 s, `D` to cycle) with a click-through countdown, so menus and hover states can be captured.
- **Automatic SDR white level:** HDRSnip reads each monitor's Windows SDR content brightness at capture time instead of relying on a hand-set value. On by default; existing installs with a customised level keep it.
- **Numbered steps:** a new editor tool (`N`) places auto-incrementing numbered markers.
- **HDR export:** HDR captures can be saved as JPEG XR (`.jxr`) from **Save as** — the original float pixels in linear scRGB, losslessly, with any markup composited at SDR white.
- **Re-tone-map in the editor:** click the *HDR tone-mapped* badge to switch curve or SDR white level with a live preview. Marks and undo history are untouched.
- Editor zoom is now in screen pixels, so 100% is pixel-exact on scaled displays.

### Faster and lighter

- The capture daemon now builds its DXGI session at launch, so the first snip no longer pays a one-off setup delay.
- A static desktop is captured immediately instead of waiting for a frame that is not coming.
- The clipboard PNG is encoded once, off the UI thread, and reused for auto-save.
- Both processes release capture buffers once a snip is finished instead of holding them while idle.

### Fixes

- Turning HDR on or off no longer leaves the next captures too dark or too bright until restart.
- The daemon exits with the tray app even if the tray app crashes, instead of lingering in the background.
- Global hotkeys are released while Settings is open, so a hotkey can be re-recorded rather than starting a capture.
- A failed auto-save no longer discards the capture; the toast and editor say so.
- Two captures in the same second no longer overwrite each other on disk.
- Marked-up captures keep the source DPI, so they paste at the same size as unmarked ones.
- `--edit` on a JPEG or BMP saves back in that format rather than writing PNG data under the old name.
- Exit asks before discarding unexported markup, and honours the answer.
- In the crop tool, Undo with nothing to undo no longer reverts the last handle drag.
- A config file with a missing save folder or invalid white level is repaired on load.

### Housekeeping

- Now targets .NET 10 (LTS); .NET 8 leaves support in November 2026.
- `System.Drawing.Common` package reference removed; the Windows Desktop runtime already provides it.


## 1.2.0

### Markup editor

The post-capture window is now a lightweight markup tool.

- **Tools:** Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Text, Pixelate and Crop, each with a single-key shortcut and a status-bar hint.
- **Editable marks:** select any mark to move it, nudge it with the arrow keys, recolour or resize it, or delete it. Double-click text to edit it in place.
- **Colour and size flyout:** twelve swatches and a size slider with a live preview. Settings are remembered per tool.
- **Non-destructive crop:** drag the handles or draw a new area, with rule-of-thirds guides. Crop again later to widen it.
- **Undo and redo** for every mark, move, restyle and crop.
- **Shift constraints:** straight pen lines, 45° snapping for lines and arrows, squares and circles for shapes.
- **Exact export:** copy and save flatten the marks at 1:1 with the capture. An unmarked capture round-trips byte for byte.
- **Nothing lost by accident:** closing with unexported markup asks first, and a new capture arriving while the editor holds unexported markup opens in a second window.
- `HDRSnip.exe --edit image.png` opens the editor on an existing file.

### Housekeeping

- Consistent line endings across the repository via `.gitattributes`, and `dotnet format` passes on the whole solution.
- Store listing text updated for the editor.

## 1.1.1

- Store autostart uses a Windows startup task.
- Store listing art generated from the same vector definition as the app icon.
- Privacy policy and `runFullTrust` justification for Store submission.
