<p align="center"><img src="Support/AppIcon.png" width="128" alt="OpenShelf icon"></p>

# OpenShelf

An open-source macOS menu-bar utility for working with files mid-drag.

Start dragging any file and tap a key — a wheel appears under the pointer. Drop
the file on a wedge and it's done, saved right next to the original.

- **⇧ Convert wheel:** images, PDFs, documents, audio, video, subtitles and
  archives into the formats that make sense for them (JPG, PNG, HEIC, AVIF,
  WEBP, PDF, DOCX, TXT, MP3, WAV, GIF, ZIP, …)
- **⇧⌥ Tools wheel:** for images — Crop, Add Background, Annotate, Redact,
  Edit, Compress, Metadata, Read QR; for PDFs — Combine, Pages, Split,
  Compress, Metadata; for video and audio — Trim, Speed, Crop, Compress, Mute
- **⌃ Actions wheel:** Shelf · Add to Shelf · AirDrop · Copy · Move · Copy Path · Terminal
- **Shelves:** stacked preview, expandable grid/list, multi-select, drag out to
  any app, Quick Look (Space), dock to the left/right screen edge, multiple shelves
- Works system-wide with **no Accessibility permission**
- No network access, no telemetry, no dependencies

See [docs/SPEC.md](docs/SPEC.md) for the full behaviour and roadmap. Optional:
`brew install ffmpeg webp` adds MP3, GIF and WebP output.

## Install

1. Download **`OpenShelf-<version>.dmg`** from the
   [latest release](https://github.com/Manan-Santoki/OpenShelf/releases/latest).
2. Open it and drag **OpenShelf** onto the **Applications** folder.
3. Open OpenShelf from Applications. It lives in the menu bar (the wheel icon),
   and a short welcome guide appears the first time.

**"Apple could not verify OpenShelf…"** The app isn't notarized by Apple yet,
so macOS asks once on first launch:

- **macOS 15 Sequoia and later:** click **Done**, open **System Settings →
  Privacy & Security**, scroll down to the message about OpenShelf and click
  **Open Anyway**, then confirm.
- **macOS 14 Sonoma:** right-click OpenShelf in Applications → **Open** → **Open**.
- Or, in Terminal: `xattr -dr com.apple.quarantine /Applications/OpenShelf.app`

Requires macOS 14 Sonoma or later, on Apple silicon or Intel. A `.zip` of the
app is also attached to each release.

## How to use

Everything starts with a drag. Press on a file in Finder, on the Desktop, or in
any app that lets you drag files, and **keep holding** while you tap a key.

1. Start dragging one or more files.
2. While still dragging, tap a key. You don't need to keep holding it:
   - **⇧ Shift** opens the **Convert** wheel
   - **⇧⌥ Shift-Option** opens the **Tools** wheel (or add ⌥ after ⇧ to switch)
   - **⌃ Control** opens the **Actions** wheel
3. Move onto a wedge. It lights up and the centre shows what will happen; before
   you hover, the centre shows the file's size.
4. Let go on the wedge. Releasing anywhere else, or pressing **Esc**, cancels.

Tip: on a trackpad, press the key *after* the drag has started. Holding ⌃
before you click turns the click into a right-click.

### Convert (⇧)

The wheel only offers formats that make sense for what you're dragging, and
never the format it's already in. The result is saved **next to the original**
and nothing is ever overwritten (`Photo.jpg` → `Photo.png`, `Photo 2.png`, …).
A small card next to the pointer shows progress; click its **×** to cancel.

| Drag a… | Convert to |
|---|---|
| Image (JPG, PNG, HEIC, …) | PNG, JPG, WEBP, HEIC, TIFF, AVIF, BMP, PDF, DOCX, ZIP |
| PDF | DOCX, JPG, PNG, TXT, ZIP |
| Document (TXT, RTF, DOCX, HTML, Markdown) | PDF, DOCX, TXT, RTF, HTML, ZIP |
| Audio | MP3, M4A, WAV, AIFF, FLAC, ZIP |
| Video | MP4, MOV, M4A, GIF, MP3, ZIP |
| Subtitles (SRT, VTT) | TXT, SRT, VTT, ZIP |
| Archive (ZIP, TAR, GZ, 7Z, RAR) | Unzip into a folder |
| Folder or anything else | ZIP |

Drag several files at once to convert them all; mixed selections show only the
formats every file supports. MP3, GIF and WebP need `brew install ffmpeg webp`.

### Tools (⇧⌥)

| Drag a… | Tools |
|---|---|
| Image | **Compress** · **Metadata** · **Edit** · **Annotate** · **Add BG** · **Crop** · **Redact** · **Read QR** |
| Several images or PDFs | **Combine** into one PDF |
| PDF | **Pages** (reorder, rotate, delete) · **Split** · **Compress** · **Metadata** |
| Video | **Trim** · **Speed** · **Crop** · **Compress** · **Mute** |
| Audio | **Trim** · **Speed** |

Most tools open a small editor. Make your changes and press the button at the
bottom right (or Return); a copy is saved next to the original with a suffix,
like `Photo Cropped.jpg`, so you can drag the result straight back into another
wheel. **Read QR**, **Combine** and **Mute** run immediately without an editor.

### Actions (⌃)

| Wedge | What it does |
|---|---|
| **Shelf** | Puts the files on a new floating shelf next to the pointer |
| **Add to Shelf** | Adds them to the shelf you used last (shown when a shelf is open) |
| **AirDrop** | Opens AirDrop with the files |
| **Copy** | Copies the files; paste in Finder to duplicate them |
| **Move** | Asks for a folder and moves the files there |
| **Copy Path** | Copies the full path of each file |
| **Terminal** | Opens Terminal in the file's folder |

### Shelves

A shelf holds files for later without moving them. Drop more files onto it at
any time, and drag them out to any app or folder when you need them.

- **Collapsed**, it shows a stack with a count. Drag the stack to drag every file.
- **Expand** (chevron or double-click) for a grid or list. Click, ⌘-click and
  ⇧-click to select; drag a selection out together.
- **Space** previews with Quick Look, **⌫** removes from the shelf, **Return**
  opens, **⌘A** selects all, **Esc** collapses, **⌘W** closes. Right-click for
  more, including Share.
- Drag a shelf near the left or right edge of the screen to dock it there.
- Closing a shelf never touches your files. **New Shelf** and **Close All
  Shelves** are in the menu-bar menu.

### Settings

Click the OpenShelf wheel in the menu bar → **Settings…** to change the key for
each wheel, remove files from a shelf after dragging them out, or open
OpenShelf at login. **How to Use OpenShelf** in the same menu shows the
welcome guide again.

## Tech stack

| Layer | Choice |
|---|---|
| Language | Swift 6 (strict concurrency) |
| UI | SwiftUI for views; AppKit for panels, drag sessions, menu bar, Quick Look |
| Frameworks | ImageIO, Core Image, PDFKit, AVFoundation, QuickLookThumbnailing, Quartz, ServiceManagement, Observation |
| Build | `make` (calls `swiftc` directly) · `Package.swift` for SwiftPM/Xcode |
| Tests | Swift Testing |
| Minimum OS | macOS 14 Sonoma |
| License | MIT |

## Build & run

Requires Xcode or the Xcode Command Line Tools.

```sh
make run      # build, bundle into build/OpenShelf.app, and launch
make app      # just build the .app
make test     # run the test suite (needs a working SwiftPM)
make release  # universal build, zipped for a GitHub release
make dmg      # drag-to-Applications installer (after make release; needs create-dmg)
```

You can also open `Package.swift` in Xcode.

## Project layout

```
Sources/OpenShelf/
  App/            entry point, app delegate, preferences
  DragDetection/  system-wide drag + modifier polling
  Wheel/          wheel items, geometry, overlay panel, SwiftUI rendering
  Convert/        format catalog, converters, progress runner
  Tools/          tool list, launcher, editor window host
  Tools/Editors/  image editors (crop, add background, annotate, …)
  Tools/PDF/      PDF tools (combine, pages, split, compress, metadata)
  Tools/Media/    video and audio tools (trim, speed, crop, compress, mute)
  Shelf/          shelf model, floating panel, docking, layout
  Shelf/Views/    SwiftUI shelf UI and the AppKit drag source
  Support/        file actions, Quick Look
  Settings/       settings window
```

## How drag detection works

macOS doesn't announce drags from other apps. OpenShelf polls three pieces of
global state that need no special permission: the primary mouse button, the
held modifier keys, and the change count of the system drag pasteboard. When
the button is down, the drag pasteboard has changed since it went down and holds
file URLs, a file drag is in progress; if a wheel's modifier combination is then
held for ~70 ms, the wheel opens. The wheel itself is an ordinary drop target.

## Contributing

Issues and pull requests are welcome. Please keep OpenShelf free of third-party
assets and code you don't have the rights to — all artwork, including the app icon (`scripts/make-icon.swift`), is SF Symbols and
SwiftUI drawing.
