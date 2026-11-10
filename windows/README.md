# OpenShelf for Windows

The Windows port of OpenShelf: drag files, hold the trigger, drop on a wheel wedge to convert, copy, share or shelve them.
C# on .NET 10 with WPF. It runs on Windows 10 22H2 and Windows 11. `PLAN.md` has the architecture and status; `../docs/SPEC.md`
is the behaviour reference.

## Install

1. Download `OpenShelf-win-Setup.exe` from the newest `win-v*` release on the
   [Releases page](https://github.com/Manan-Santoki/openshelf/releases) (use `OpenShelf-win-arm64-Setup.exe` on an ARM PC).
2. Run it. There are no questions: it installs for the current user only (no administrator prompt) into
   `%LOCALAPPDATA%\OpenShelfApp`, adds a Start menu entry and starts OpenShelf.
3. OpenShelf lives in the notification area. Uninstall from Settings, Apps, Installed apps.

A silent install for scripts: `OpenShelf-win-Setup.exe --silent` (add `--installto <folder>` to choose the folder).

`OpenShelf-win-Portable.zip` is the same app without the installer. Portable copies do not update themselves.

### What you see on first run (unsigned builds)

Until the installer is code signed, Windows does not know the publisher:

- **Browser:** Edge or Chrome may say the file "isn't commonly downloaded". Choose Keep, then Keep anyway.
- **SmartScreen:** "Windows protected your PC. Microsoft Defender SmartScreen prevented an unrecognized app from starting."
  Click **More info**, then **Run anyway**. The publisher shows as "Unknown publisher".
- No UAC prompt appears, because nothing is installed system-wide.

The warning fades as a file or certificate builds reputation, and disappears for a validly signed installer from a signer SmartScreen
already trusts (see below).

### Updates

Installed copies check GitHub Releases for a newer `win-v*` release 30 seconds after launch and then daily, unless
Settings, Updates, "Automatic updates" is off. A found update downloads in the background and OpenShelf asks whether to
restart into it; if you say no, it installs the next time OpenShelf starts. **Check for Updates…** in the tray menu checks
on demand. A copy that was not installed with the installer (a build folder, the portable zip) answers "Updates only work in
the installed app" instead of failing. Updates are delta packages when possible, so they are usually small.

## Build from source

You need the .NET 10 SDK on Windows.

```powershell
dotnet build windows\OpenShelf.sln
dotnet test  windows\OpenShelf.sln
dotnet run --project windows\OpenShelf.App
```

Tests that need ffmpeg, LibreOffice, calibre or the network skip themselves when those are missing
(`OPENSHELF_NETWORK_TESTS=1` turns on the download link checks).

A copy run from a build folder is "not installed" as far as the updater is concerned.

### Project layout

`OpenShelf.Update` wraps [Velopack](https://velopack.io) (MIT) behind `UpdateService`: check, download with progress, apply and
restart, and the automatic check. It has no UI; the app supplies prompts as delegates (`OpenShelf.App/UpdateWiring.cs`).
`OpenShelf.App/Program.cs` is the entry point and runs Velopack's install and update hooks before WPF starts.

## Icons

Every icon in the app comes from [Lucide](https://lucide.dev) (ISC licence; some icons derived from Feather are MIT), pinned to
`lucide-static` 1.71.0. The path data is embedded in `OpenShelf.Ui/Lucide.g.cs` and drawn by `IconView`, so there is no network or SVG
parsing at run time. The licence text is `OpenShelf.Ui/LICENSE-lucide.txt` and must ship with the app. To add an icon, add its name to
`scripts/gen-lucide.mjs` and run it against an unpacked `lucide-static` package (the header of that script has the commands). Wheel
entries carry the icon name in `WheelEntry.Glyph` ("crop"); a value that is not a Lucide name is drawn in the old Segoe icon font.

## Make a release

Local test pack (any version number):

```powershell
.\windows\scripts\package.ps1 -Version 0.1.0              # x64
.\windows\scripts\package.ps1 -Version 0.1.0 -Arch both   # x64 and arm64
```

This publishes self-contained builds and writes to `windows\.artifacts\release`:

| File | Purpose |
|---|---|
| `OpenShelf-win-Setup.exe` | The installer (x64) |
| `OpenShelf-win-Portable.zip` | Portable build |
| `OpenShelfApp-<version>-full.nupkg`, `-delta.nupkg` | Update packages |
| `releases.win.json`, `RELEASES`, `assets.win.json` | The update feed |
| `OpenShelf-win-arm64-*`, `releases.win-arm64.json` | The same for ARM64 (own channel, `-Arch arm64` or `both`) |

The version is stamped into the executable and the update packages. The package id is `OpenShelfApp` on purpose: Velopack owns
`%LOCALAPPDATA%\<id>` and deletes it on uninstall, while OpenShelf keeps logs, models and tools in `%LOCALAPPDATA%\OpenShelf`.
The app icon is `OpenShelf.App/Assets/OpenShelf.ico`, generated from `Support/AppIcon.png` by `scripts/make_icon.py` (frames from 40 px up are the real artwork; the 16, 20, 24 and 32 px frames are a simplified redraw of the same mark so it stays legible in the taskbar and tray). The same file is embedded in `OpenShelf.Ui` and gives every window, and the tray, its icon.

To publish a release:

```bash
git tag win-v0.1.0
git push origin win-v0.1.0
```

`.github/workflows/windows-release.yml` then tests, packs both architectures, uploads to a GitHub Release named
"OpenShelf for Windows 0.1.0" and publishes it. The Windows release is deliberately **not** marked "Latest": the Mac updater
reads `releases/latest/download/appcast.xml`, which a Windows release does not have. A version with a dash (`win-v0.2.0-beta.1`)
becomes a prerelease, which installed copies ignore.

`windows-ci.yml` builds, tests and does an unsigned trial pack on every push and pull request that touches `windows/`.

### Code signing

Signing is off unless the repository has a signing secret. `package.ps1` and the release workflow pick it up on their own.

| Option | Cost | Setup |
|---|---|---|
| **Azure Trusted Signing** (now "Artifact Signing") | about $10 a month | Create the account and a certificate profile (identity validation needed; availability depends on country and organization type). Secrets: `AZURE_TRUSTED_SIGNING_METADATA` (the contents of `metadata.json`), `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`. |
| **SignPath Foundation** | free for open source | Apply for the OSS program, then sign in the workflow with SignPath's GitHub action instead of the built-in step. |
| **OV or EV certificate** from a CA | $100 to $500 a year | Put the `.pfx` in the secrets `WINDOWS_CERT_PFX_BASE64` (base64 of the file) and `WINDOWS_CERT_PASSWORD`. Since 2023 new certificates must live on a hardware token or cloud HSM, so a plain `.pfx` mostly works for older certificates; for the others use the CA's cloud signing tool through `-SignParams`. An EV certificate removes the SmartScreen warning at once; OV builds reputation over time. |

Locally: `package.ps1 -Version 0.1.0 -SignParams '/a /fd sha256 /tr http://timestamp.digicert.com /td sha256'` (any signtool
arguments) or `-AzureSignFile path\to\metadata.json`.

### winget

Not set up. Once a signed (or at least stable) installer exists, a winget manifest for `OpenShelf-win-Setup.exe`
(`InstallerType: exe`, switches `--silent`) can be submitted to `microsoft/winget-pkgs` with `wingetcreate`. Because the app
updates itself, set the manifest's `UpgradeBehavior: uninstallPrevious` or leave upgrades to the in-app updater.
