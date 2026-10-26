<p align="center"><img src="Support/AppIcon.png" width="128" alt="OpenShelf icon"></p>

# OpenShelf

An open-source macOS menu-bar utility for working with files mid-drag.

Start dragging any file and tap a key — a wheel appears under the pointer. Drop
the file on a wedge and it's done, saved right next to the original.

<p align="center"><img src="docs/images/wheels.png" alt="The Convert, Tools and Actions wheels" width="900"></p>

- **⇧ Convert wheel:** images, PDFs, documents, spreadsheets, slides, audio,
  video, subtitles and archives into the formats that make sense for them (JPG,
  PNG, HEIC, AVIF, WEBP, PDF, DOCX, Markdown, EPUB, PPTX, XLSX, CSV, MP3, OGG,
  MKV, WebM, GIF, ZIP, TAR.GZ, …), with the codec, quality, size and
  bitrate yours to choose
- **⇧⌥ Tools wheel:** for images — Crop, Resize, Remove Background, Add
  Background, Annotate, Redact, Edit, Compress, Metadata, Read QR, OCR; for PDFs —
  Combine, Pages, Split, Compress, Metadata, OCR; for video and audio — Trim,
  Speed, Crop, Rotate, Split, Merge, Compress, Mute
- **⌃ Actions wheel:** Shelf · Add to Shelf · AirDrop · Copy · Move · Copy Path · Terminal
- **Shelves:** stacked preview, expandable grid/list, multi-select, drag out to
  any app, Quick Look (Space), dock to the left/right screen edge, multiple shelves
- Works system-wide with **no Accessibility permission**
- No telemetry; the only network access is checking GitHub for updates, which
  you can turn off, and downloading an extra tool when you click Install
- **Updates itself:** new versions install from the app (Sparkle), signed so
  only genuine OpenShelf releases are accepted
- **Extra formats, one click away:** ffmpeg adds MP3, OGG, WebP, MKV, WebM,
  AVI and WMV; LibreOffice adds PowerPoint, XLS and ODS; Pandoc converts
  Markdown and Word documents its own way. Install any of them from
  Settings → Extra Formats (no Homebrew or administrator needed), or use the
  copies you already have from Homebrew

See [docs/SPEC.md](docs/SPEC.md) for the full behaviour and roadmap.

## Install

1. Download **`OpenShelf-<version>.dmg`** from the
   [latest release](https://github.com/Manan-Santoki/OpenShelf/releases/latest).
2. Open it and drag **OpenShelf** onto the **Applications** folder.
3. Open OpenShelf from Applications. It lives in the menu bar (the wheel icon),
   and a hands-on tour opens the first time (see below).

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

**Take the tour.** The first time OpenShelf opens, a guided tour walks you
through everything with practice files it makes for you: a photo, Markdown
notes, a PDF, a video and a letter. Each step shows the keys to press and
waits until you've done it for real: convert a photo, turn Markdown into a
PDF, crop with the Tools wheel, split a PDF, trim a video, put files on a
shelf, open a wheel without dragging, and choose a conversion's settings.
Skip any step or the whole tour; pick it up again from the menu bar (**Take
the Tour…**) or Settings → General, where **Start Over** begins afresh. Your
own files are never touched, and the practice folder can be removed at the end.

Everything starts with a drag. Press on a file in Finder, on the Desktop, or in
any app that lets you drag files, and **keep holding** while you tap a key.

1. Start dragging one or more files.
2. While still dragging, tap a key. You don't need to keep holding it:
   - **⇧ Shift** opens the **Convert** wheel
   - **⇧⌥ Shift-Option** opens the **Tools** wheel (or add ⌥ after ⇧ to switch)
   - **⌃ Control** opens the **Actions** wheel
3. Move onto a wedge. It lights up and the label above the wheel shows what will
   happen; before you hover, it shows the file's size.
4. Let go on the wedge. Releasing anywhere else, or pressing **Esc**, cancels.
   Or, while still dragging, let go of the key and use the arrow keys and
   Return. (⌃ or ⌘ plus an arrow is a macOS shortcut, ⌃→ switches desktops,
   so the arrows wait until those keys are up.)

Wheels with more than seven options show the everyday ones first, six at a
time. Rest on **More**, the wedge at about 10 o'clock, for a moment to turn the
page; **Back** in the same place turns back. Dropping on More or Back turns the
page too and keeps the wheel open, so you can click the option you want. With
the keyboard, choose More or Back and press Return.

**Without dragging:** select files in Finder or on a shelf and press
**⌃⌥Space**. The Convert wheel opens at the pointer; use the arrow keys and
**Return** (or click a wedge), **Tab** to switch to Tools and Actions, **Esc**
to close. A focused shelf's selection takes priority; when Settings or an
editor is in front, OpenShelf uses the most recently used shelf's selection.
Otherwise it reads Finder's selection, asking for Automation permission the
first time. Change or turn off the shortcut in Settings → Wheels.

**Quick keyboard choices:** hold **⌘ Command** to show each wedge's number,
then press that number to run it. **⌘0** turns More or Back. Numbering starts
again on each page; **⌘⌥number** opens conversion settings where available.

Tip: on a trackpad, press the key *after* the drag has started. Holding ⌃
before you click turns the click into a right-click.

### Convert (⇧)

The wheel only offers formats that make sense for what you're dragging, and
never the format it's already in. The result is saved **next to the original**
and nothing is ever overwritten (`Photo.jpg` → `Photo.png`, `Photo 2.png`, …).
A small card next to the pointer shows progress; click its **×** to cancel.

<p align="center"><img src="docs/images/progress.png" alt="Progress card next to the pointer" width="300"></p>

| Drag a… | Convert to |
|---|---|
| Image (JPG, PNG, HEIC, SVG, …) | PNG, JPG, WEBP², HEIC, TIFF, AVIF, BMP, PDF, DOCX, ZIP |
| PDF | DOCX, JPG, PNG, TXT, Markdown, EPUB, PPTX, XLSX, ZIP |
| Document (TXT, RTF, RTFD, DOCX, DOC, ODT, HTML, Markdown) | PDF, DOCX, TXT, Markdown, EPUB, HTML, RTF, ZIP |
| Spreadsheet (CSV, TSV, XLSX) | XLSX, CSV, TSV, PDF, ODS¹, ZIP |
| Spreadsheet (XLS, ODS)¹ | XLSX, CSV, PDF, ODS, ZIP |
| Slides (PPTX, PPT, ODP)¹ | PDF, JPG, PNG (one image per slide), ZIP |
| Audio (MP3, M4A, WAV, AIFF, FLAC, OGG², Opus², WMA², …) | MP3², M4A, WAV, AIFF, FLAC, OGG², Opus², WMA², ZIP |
| Video (MP4, MOV, MKV², WebM², AVI², WMV², …) | MP4, MOV, M4A, GIF, MP3², MKV², WebM², AVI², WMV², ZIP |
| Subtitles (SRT, VTT) | TXT, SRT, VTT, ZIP |
| Archive (ZIP, TAR, GZ, 7Z, RAR) | Unzip into a folder |
| Folder or anything else | ZIP, TAR, TAR.GZ |

Drag several files at once to convert them all; mixed selections show only the
formats every file supports. Several files sent to ZIP, TAR or TAR.GZ go into
one archive.

¹ Needs LibreOffice. ² Needs ffmpeg (`brew install webp` also adds WebP).

Both are free and optional. Install them from **Settings → Extra Formats**: one
click downloads the official build (ffmpeg from
[ffmpeg.martin-riedl.de](https://ffmpeg.martin-riedl.de), LibreOffice from
[libreoffice.org](https://www.libreoffice.org)), checks its checksum and its
publisher's code signature, and installs it for you alone, without an
administrator password. When ffmpeg's server is down, the same signed build
comes from OpenShelf's mirror on GitHub, checked the same way; when that's
down too (or libreoffice.org is), and you have Homebrew, **Install with
Homebrew** runs `brew install` for you. Copies from Homebrew (`brew install ffmpeg`,
`brew install --cask libreoffice`) are used too and take precedence. Without
them those wedges are hidden, and dragging a file only they can open (an MKV,
a PowerPoint deck) says which one it needs. GIFs from video are made without
ffmpeg, though it makes slightly cleaner ones when installed. Keynote files
can't be converted, and Apple's Pages format isn't offered.

#### Markdown and Word documents

Markdown is rendered, not copied as text: headings, emphasis, lists, quotes,
code blocks, tables, links, pictures beside the file, footnotes and math come
out as the real thing in PDF, Word, HTML, EPUB and RTF, and as tidy plain text
in TXT. Besides CommonMark and GitHub's tables and task lists, OpenShelf reads
the Pandoc extensions people use most: definition lists, simple and multi-line
tables, line blocks, `$math$`, smart quotes and dashes, and a YAML `title`.
Word files get Word's own heading styles, numbered lists and footnotes.
Pictures on the web are never downloaded; they're shown by their description.

If [Pandoc](https://pandoc.org) is installed, OpenShelf uses it instead for
Markdown to Word, HTML, EPUB and RTF, and for
Word, ODT and HTML to Markdown and EPUB. Pandoc's PDFs also need a typesetter
such as MacTeX, Tectonic or Typst; without one, PDFs keep OpenShelf's own
layout. With LibreOffice installed, Word and ODT documents become PDFs through
it, keeping their page layout. Should either fail, the built-in conversion
runs instead. Pandoc downloads pictures a document links to on the web.
Install Pandoc from **Settings → Extra Formats** like ffmpeg and LibreOffice:
OpenShelf downloads its official build from GitHub, checks the checksum GitHub
lists and John MacFarlane's code signature, and installs it for you alone.
Homebrew's copy (`brew install pandoc`) is used too.

#### Output settings

Out of the box, every conversion picks its own encoding. To choose instead,
open **Settings → Output**:

- **Video** (MP4, MOV, MKV, WebM, AVI, WMV): the codec (H.264 or HEVC, for
  MP4, MOV and MKV), the quality (Best, Balanced, Smaller, or a custom bitrate
  in Mb/s) and the largest size (4K, 1080p, 720p or 480p; videos are never
  enlarged and keep their shape). HDR videos stay HEVC to keep their colours.
- **Audio:** a fixed bitrate, 96–320 kb/s, for MP3, M4A, OGG, Opus and WMA and
  for the sound in converted videos. Automatic follows the original's quality.
- **Images:** the quality of JPEG, HEIC, AVIF and WebP, and whether the
  camera, date and location are kept.
- **GIFs from videos:** the width (480, 720 or 1080 px, or the video's own up
  to 1920) and the frame rate (10–24 fps).

Automatic, the default for each, converts exactly as before. **Reset to
Automatic** goes back to it.

**Just this once:** hold **⌥ Option** as you drop on a format, or press
**⌥Return** (or ⌥-click) in the keyboard wheel. A small window shows the
settings for that format, filled in from Settings → Output, with an estimate
of the size for video. Click **Convert** to use them for this conversion only,
or tick **Use as default** to keep them. Formats with nothing to choose (PNG,
WAV, ZIP, …) convert right away.

### Tools (⇧⌥)

| Drag a… | Tools |
|---|---|
| Image | **Compress** · **Metadata** · **Edit** · **Annotate** · **Add BG** · **Crop** · **Resize** · **Remove BG** · **Redact** · **Read QR** · **OCR** |
| Several images or PDFs | **Combine** into one PDF |
| PDF | **Pages** (reorder, rotate, delete) · **Split** · **Compress** · **Metadata** · **OCR** |
| Video | **Trim** · **Speed** · **Crop** · **Rotate** · **Split** · **Compress** · **Mute** |
| Audio | **Trim** · **Speed** · **Split** |
| Several videos, or several audio files | **Merge** into one file |

**OCR:** drop images, PDFs, or a mixture on OCR, choose **Searchable PDF** or
**Text**, then Save. Recognition runs on this Mac in your supported system
languages. Existing PDF text is preserved; scanned pages are read individually.
Results are saved beside each original as `Name OCR.pdf` or `Name OCR.txt`.

Most tools open a small editor. Make your changes and press the button at the
bottom right (or Return); a copy is saved next to the original with a suffix,
like `Photo Cropped.jpg`, so you can drag the result straight back into another
wheel. **Read QR**, **Combine** and **Mute** run immediately without an editor.

Every editor works on several files at once. Drop five photos on **Crop**, set
the crop once, and **Same for all** applies it to every one (the top half stays
the top half, whatever their sizes); turn it off and click through the
filmstrip to give each file its own edit. Annotate, Redact and PDF Pages keep
separate edits per file, and Trim and Split start with each file on its own.

<p align="center"><img src="docs/images/editors.png" alt="Crop, Add Background and Annotate editors" width="900"></p>
<p align="center"><img src="docs/images/trim.png" alt="Trim editor for video and audio" width="560"></p>

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

<p align="center"><img src="docs/images/shelf.png" alt="A collapsed and an expanded shelf" width="620"></p>

- **Collapsed**, it shows a stack with a count. Drag the stack to drag every file.
- **Expand** (chevron or double-click) for a grid or list. Click, ⌘-click and
  ⇧-click to select, or drag across empty space to select several at once;
  drag a selection out together. Type the start of a name to jump to a file.
- **Space** previews with Quick Look, **⌫** removes from the shelf, **Return**
  opens, **⌘A** selects all, **Esc** collapses, **⌘W** closes. Right-click for
  more, including Share.
- Drag a shelf near the left or right edge of the screen to dock it there.
  When you move the pointer away, a docked shelf slides behind the edge and
  leaves a slim tab with the file count; hover the tab, or drag files onto it,
  to bring the shelf back.
- Shelves are still there after you quit and reopen OpenShelf, even if you
  moved or renamed the files in the meantime.
- Closing a shelf never touches your files. **New Shelf** and **Close All
  Shelves** are in the menu-bar menu.

### Updates

OpenShelf checks GitHub for new versions once a day (it asks the first time).
When one is out you'll see its release notes and an **Install Update** button;
the app restarts on the new version. Closing the window means "later"; it will
remind you again. You can also choose **Check for Updates…**
from the menu bar, or turn automatic checks and installs on or off in Settings.

### Settings

Click the OpenShelf wheel in the menu bar → **Settings…** (⌘,). It opens on the
tab you used last:

- **General:** open OpenShelf at login, and take the tour again or start it
  over (also in the menu as **Take the Tour…**)
- **Wheels:** the keys that open each wheel while dragging, and the shortcut
  for Finder or shelf selections
- **Shelves:** remove files from a shelf after dragging them out, restore
  shelves at launch, hide docked shelves at the screen edge
- **Output:** how conversions encode video, audio, images and GIFs (see
  [Output settings](#output-settings))
- **Extra Formats:** install or remove ffmpeg, LibreOffice and Pandoc
- **Updates:** automatic checks and installs, and Check Now

ffmpeg and ffprobe that OpenShelf installs live in
`~/Library/Application Support/OpenShelf/Tools`; LibreOffice goes to
`~/Applications`. Remove in Settings deletes them (LibreOffice goes to the
Trash). They aren't updated automatically: OpenShelf only goes online for them
when you click Install.

## Tech stack

| Layer | Choice |
|---|---|
| Language | Swift 6 (strict concurrency) |
| UI | SwiftUI for views; AppKit for panels, drag sessions, menu bar, Quick Look |
| Frameworks | ImageIO, Core Image, PDFKit, AVFoundation, QuickLookThumbnailing, Quartz, ServiceManagement, Observation |
| Updates | [Sparkle](https://sparkle-project.org) 2 (MIT), downloaded and checksum-verified at build time |
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
  DragDetection/  system-wide drag + modifier detection
  Wheel/          wheel items, geometry, overlay panel, SwiftUI rendering
  Convert/        format catalog, converters, progress runner
  Install/        downloading, checking and installing ffmpeg and LibreOffice
  Tools/          tool list, launcher, editor window host
  Tools/Editors/  image editors (crop, add background, annotate, …)
  Tools/PDF/      PDF tools (combine, pages, split, compress, metadata)
  Tools/Media/    video and audio tools (trim, speed, crop, rotate, split, merge, compress, mute)
  Shelf/          shelf model, floating panel, docking, layout
  Shelf/Views/    SwiftUI shelf UI and the AppKit drag source
  Support/        file actions, Quick Look
  Settings/       settings window
```

## How drag detection works

macOS doesn't announce drags from other apps, and OpenShelf asks for no special
permission. Instead, a mouse-down anywhere (mouse events can be observed without
permission) wakes it, and while the button stays down it reads three pieces of
global state every frame: the primary mouse button, the held modifier keys, and
the change count of the system drag pasteboard. When the button is down and the
drag pasteboard has changed and holds file URLs, a file drag is in progress. A
quick tap of a wheel's keys opens that wheel as soon as they're released; keys
held down count once they've been steady for ~70 ms, so pressing ⇧ on the way to
⇧⌥ doesn't flash the wrong wheel. Once the button is up, OpenShelf goes idle
again. The wheel itself is an ordinary drop target.

## Releasing

Releases are automatic and driven by [CHANGELOG.md](CHANGELOG.md):

1. Add a section for the new version at the top of `CHANGELOG.md`, e.g.
   `## [1.1.0] - 2026-11-02`, describing what changed.
2. Push to `main`.

The **Release** workflow then tests and builds the universal app on a macOS
runner, makes the DMG and zip, tags `v1.1.0` and publishes a GitHub release
with that changelog section as its notes. Versions that are already tagged are
skipped. The release also carries a signed `appcast.xml`, which is how
installed copies find the update: Sparkle reads
`releases/latest/download/appcast.xml` and only installs updates signed with
the project's EdDSA key (stored as the `SPARKLE_PRIVATE_KEY` repository
secret; the public key is `SUPublicEDKey` in `Support/Info.plist`). The **CI**
workflow builds and tests every push and pull request.

## Contributing

Issues and pull requests are welcome. Please keep OpenShelf free of third-party
assets and code you don't have the rights to — all artwork, including the app icon (`scripts/make-icon.swift`), is SF Symbols and
SwiftUI drawing.
