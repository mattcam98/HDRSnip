# Microsoft Store submission — HDRSnip

**Store ID:** `9N101514P15J`  
**Identity Name:** `HDRSnipOpenSource.HDRSnip`  
**Publisher:** `CN=9F23CC6F-12A3-4502-9862-2F838EDA17E4`  
**Publisher display name:** `HDRSnip Open Source`

Manifest identity is already set in `packaging/Package.appxmanifest`.

---

## Checklist (do these in order)

### A. Build the package (on this machine)

```powershell
cd C:\Users\matt\Documents\HDRSnip
.\scripts\build-msix.ps1
```

Output: `artifacts\HDRSnip_1.0.0.0_x64.msix`

If MakeAppx is missing, the script tries to install the Windows SDK. You can also install **Visual Studio 2022** with the *Windows application packaging* workload and use **Publish → Create App Packages**.

### B. Partner Center → Start submission

Click **Start submission**, then fill each section:

#### 1. Packages
- Upload `artifacts\HDRSnip_1.0.0.0_x64.msix`
- Architecture: **x64**
- Microsoft re-signs for the Store (no cert needed from you)

#### 2. Properties
- **Category:** Productivity (or Developer tools)
- **System requirements:** Windows 10 version 1809 or later / Windows 11
- **Privacy policy URL:** optional for now (HDRSnip stores settings locally only; no accounts/telemetry). You can use your GitHub README or add a short `PRIVACY.md` later.

#### 3. Age ratings
- Run the questionnaire (screen capture tool → typically **3+** / Everyone-style)

#### 4. Store listings (English)
Use the copy below (edit freely).

**Product name:** HDRSnip  

**Short description** (≤200 chars suggested):
> HDR-aware snipping tool for Windows. Take screenshots that look correct when HDR is on — no more washed-out text.

**Description:**
```
HDRSnip is an open-source snipping tool built for Windows HDR displays.

When HDR is enabled, the built-in Snipping Tool and Print Screen often produce washed-out or overexposed screenshots. HDRSnip captures the desktop in high dynamic range (FP16 scRGB via DXGI), then tone-maps to a normal SDR image so UI text and colors stay readable when you paste or share.

Features
• Rectangular snip with frozen HDR-correct preview
• Fullscreen capture (monitor under cursor)
• Copies to clipboard by default
• Toast notification — click to open the editor and save
• Windows/OBS-style tone mapping with adjustable SDR white level
• System tray + hotkeys (Ctrl+Shift+S)

Open source (MIT): https://github.com/mattcam98/HDRSnip
```

**Screenshots** (required — at least 1, ideally 3–4):
1. Region selection overlay  
2. Toast / clipboard result of an HDR desktop (sharp text)  
3. Optional: side-by-side vs washed-out Snipping Tool  
4. Settings window  

Capture these with HDRSnip itself on your HDR monitor (1366×768 minimum; 1920×1080 is fine).

**Store logo / tiles:** already generated under `packaging/Images/` (Partner Center may also ask for a 300×300 logo — resize `logo.png`).

#### 5. Pricing
- Free (recommended for open source)

#### 6. Age / markets / discovery
- Defaults are fine for a first release

### C. Submit for certification
- Review → **Submit to the Store**
- Certification often takes hours to a few days
- You’ll get email if anything fails (common: identity mismatch, missing screenshots, capability justification)

---

## runFullTrust warning (expected)

Desktop/WPF apps packaged as MSIX **must** declare `runFullTrust`. Partner Center shows a warning; it is not a hard block if you justify it.

In the submission, open **Submission options** (or the notes / restricted capabilities section) and paste something like:

```
HDRSnip is a classic Win32/WPF desktop application packaged with MSIX (Desktop Bridge).
It requires runFullTrust to:
• Capture the desktop via DXGI Desktop Duplication (HDR screenshots)
• Register global hotkeys and run from the system tray
• Write screenshots to the clipboard and local Pictures folder
The app does not require administrator elevation and does not collect personal data.
```

Arm64 note on the Packages page is a recommendation only for this first x64 release — optional to add Arm64 later.

---

## After it’s live

- Store deep link / web URL appear on the product page  
- Updates: bump `Version` in the manifest (e.g. `1.0.1.0`), rebuild MSIX, new submission  

Questions or package build errors: re-run `.\scripts\build-msix.ps1` and share the output.
