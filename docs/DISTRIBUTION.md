# Distributing HDRSnip (Start menu + Microsoft Store)

## 1. Start menu on your PC (now)

No Store account needed. From the repo root:

```powershell
.\scripts\install-startmenu.ps1
```

This:

- Builds a self-contained `HDRSnip.exe`
- Installs to `%LOCALAPPDATA%\Programs\HDRSnip`
- Adds a **Start menu** shortcut (and Desktop unless you pass `-NoDesktop`)

Uninstall:

```powershell
.\scripts\install-startmenu.ps1 -Uninstall
```

Then open Start and search for **HDRSnip**.

---

## 2. Microsoft Store (public download)

Yes — HDRSnip can be published for others to install from the Store. Requirements and flow:

### What you need

| Item | Notes |
|------|--------|
| [Partner Center](https://partner.microsoft.com/dashboard) / [Store developer](https://aka.ms/storedeveloper) account | Individual accounts are free (as of recent Microsoft policy changes; confirm on signup) |
| Visual Studio 2022+ | Workloads: **.NET desktop** + **Windows application packaging** |
| MSIX package | Packaging project is in `packaging/` — Store submissions use MSIX |
| Listing assets | Screenshots (at least a few), description, age rating, privacy policy URL if you collect data (HDRSnip does not) |

Microsoft **re-signs** Store MSIX packages — you do not need to buy a code-signing certificate for Store-only distribution.

### Steps

1. Create a developer account and **reserve the app name** “HDRSnip” (or your chosen name) in Partner Center.
2. Open `HDRSnip.sln` in Visual Studio.
3. Update `packaging/Package.appxmanifest` **Identity**:
   - `Name` and `Publisher` must match **Partner Center → Product management → Product identity** exactly.
4. Right-click **HDRSnip.Package** → **Publish** → **Create App Packages…**
5. Choose **Microsoft Store under a new app name** (or existing reserved name) and follow the wizard to produce `.msixupload`.
6. In Partner Center: create the submission, upload the package, fill Store listing (description, screenshots from real HDR vs washed-out comparisons work well), submit for certification.

Certification usually takes from a few hours to a couple of days. After approval, the app is searchable in the Microsoft Store on Windows 10/11.

### Store policies to watch

- **Full-trust / desktop bridge** apps (like this WPF tool) are allowed; you declared `runFullTrust` in the manifest.
- Do not claim to be an official Microsoft Snipping Tool replacement in a misleading way.
- Screen capture is expected for this category; avoid capturing DRM-protected content (DXGI already blocks that).
- Provide a clear privacy statement if you ever add analytics/accounts (currently unnecessary).

### Sideload MSIX (testers, without Store)

After Visual Studio creates an `.msix` / `.msixbundle`:

```powershell
Add-AppxPackage -Path .\HDRSnip_1.0.0.0_x64.msix
```

Users may need **Developer Mode** or a trusted certificate for sideload; Store installs skip that friction.

---

## 3. GitHub Releases (optional, parallel)

Many open-source Windows apps also ship a zip/exe on GitHub Releases via `dotnet publish`. That is fine alongside the Store; just don’t confuse users about which channel auto-updates (Store does; GitHub does not unless you add an updater).
