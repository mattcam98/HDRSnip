# Changelog

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
