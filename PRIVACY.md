# Privacy policy

**HDRSnip — last updated 2 October 2026**

HDRSnip does not collect, store, or transmit any personal information.

## What HDRSnip does with your data

HDRSnip captures screenshots. Everything it produces stays on your computer:

| Data | Where it goes |
|---|---|
| Screenshots | Your clipboard, and — only if you save them — a folder you choose (by default `Pictures\HDRSnip`) |
| A temporary thumbnail | `%TEMP%\HDRSnip`, used solely to show a preview inside the capture notification, and overwritten by the next capture |
| Images you drag out of the editor | `%TEMP%\HDRSnip\drag`, so the app you drop onto can read the file. Emptied every time HDRSnip starts and exits |
| Recent captures | Held in memory only, for the tray's *Recent captures* list. Never written to disk, and gone when HDRSnip exits |
| Text you copy out of a capture | Recognised on your PC by the text-recognition engine built into Windows, then placed on your clipboard |
| Your settings | `%LOCALAPPDATA%\HDRSnip\config.json` — save folder, tone-mapping choice, hotkeys |
| Error details | `%LOCALAPPDATA%\HDRSnip\errors.log`, written only when something fails |

Nothing in that list is transmitted anywhere.

## What HDRSnip does not do

- **No network access.** HDRSnip makes no internet connections of any kind. It has no update
  checker, no crash reporting, no analytics and no telemetry.
- **No accounts.** There is nothing to sign in to.
- **No advertising**, and no advertising identifiers.
- **No third parties.** No data is shared with anyone, because no data leaves your machine.
- **No background capture.** Screens are only read when you press a capture hotkey or choose
  a capture command from the notification-area menu.

## Screen capture and protected content

HDRSnip reads the desktop through the Windows Desktop Duplication API. That API enforces
Windows' own protected-content rules, so DRM-protected video appears blank in a capture.
HDRSnip does not attempt to work around this.

## Deleting your data

Everything HDRSnip writes can be removed by deleting these folders:

- `%LOCALAPPDATA%\HDRSnip` — settings and error log
- `%TEMP%\HDRSnip` — the notification thumbnail and any images staged for drag-out
- Your chosen screenshot folder — the screenshots you saved

Uninstalling HDRSnip through Windows Settings removes the application itself.

## Children

HDRSnip is a general-purpose utility. It collects no information from anyone, including
children.

## Changes to this policy

Any change will be published in this file, and its revision history is publicly visible in
the project's Git history.

## Contact

Questions or concerns: open an issue at
<https://github.com/mattcam98/HDRSnip/issues>.

## Source

HDRSnip is open source under the MIT licence. Every claim above can be verified by reading
the code at <https://github.com/mattcam98/HDRSnip>.
