# OpenShelf — Product Spec

OpenShelf is an open-source macOS menu-bar utility that turns any file drag into
a launcher: tap a modifier key mid-drag and a radial **wheel** appears under the
pointer; drop the files on a wedge to act on them. One of those actions puts the
files on a **shelf** — a small floating window that holds files so you can drag
them out again later.

This document describes behaviour only. OpenShelf is written from scratch from
this spec; it does not contain or derive from any other app's code or assets.

## 1. Drag detection

- Works system-wide (Finder, Desktop, browsers, any app that drags file URLs)
  without Accessibility or Input Monitoring permissions.
- A drag is recognised when the primary mouse button is down and the system drag
  pasteboard has changed since the button went down and contains file URLs.
- While a file drag is in progress, pressing a configured modifier combination
  opens the matching wheel at the pointer. A *tap* is enough: the wheel stays
  open until the drag ends (drop anywhere, or Esc).
- Each wheel has its own configurable modifier combination (see §5).

## 2. The wheel

- Circular overlay centred on the pointer: a hub in the middle and N wedges
  ("petals") around it, wedge 0 at 12 o'clock, clockwise.
- Hovering a wedge highlights it (with a haptic tick on Force Touch trackpads)
  and shows its name in the hub; the hub otherwise shows the dragged file's size
  ("1.2 MB"), or count and total size for several files ("3 · 4.2 MB").
- Dropping on a wedge runs its action with all dragged files. Dropping on the
  hub or outside the wheel does nothing.
- Keyboard, while still dragging: ←/↑ and →/↓ move the highlight round the
  wheel (repeating when held), Return runs it, Esc cancels. After Return the
  wheel hides but quietly catches the eventual mouse-up, so the files aren't
  also dropped wherever the pointer happens to be.
- Blooms out from the pointer with a spring, petals staggered; shrinks away
  after a drop. Respects Reduce Motion.
- Actions confirm themselves with a small toast next to the pointer ("Copied
  path", "Moved 2 files to Desktop"), announced to VoiceOver. Actions that open
  their own UI (shelf, AirDrop, Terminal) don't toast.
- Stays on top of everything, on every Space, including full-screen apps.

### 2.1 Wheels

| Wheel | Default trigger | Contents |
|---|---|---|
| Convert | ⇧ Shift | Output formats for the dragged files (§2.3) |
| Tools | ⇧⌥ Shift-Option | Image tools that open an editor (§2.4) |
| Actions | ⌃ Control | File actions and shelves (§2.2) |

Adding a key mid-drag switches wheels in place (⇧ → ⇧⌥ swaps Convert for Tools).
If a wheel has nothing for the dragged files, a toast says so instead.

### 2.2 Actions wheel

| Action | Behaviour |
|---|---|
| Shelf | Put the files on a new shelf next to the pointer |
| Add to Shelf | Add the files to the most recently used shelf (only shown when one exists) |
| AirDrop | Open the AirDrop sharing sheet with the files |
| Copy | Put the files on the clipboard (paste in Finder to copy them) |
| Move | Ask for a destination folder and move the files there |
| Copy Path | Copy POSIX paths, one per line |
| Terminal | Open Terminal at the file's folder (or the folder itself) |

### 2.3 Convert wheel

Wedges are chosen by file type; only formats every dragged file supports are
offered, and the file's own format is left out. Results are saved beside the
original and never overwrite (`Name.jpg`, `Name 2.jpg`, …). A progress toast by
the pointer shows "Converting to JPG · 1 of 3 saved" with a cancel button.

| Input | Outputs | Engine |
|---|---|---|
| Image | PNG, JPG, WEBP*, HEIC, TIFF, AVIF, BMP, PDF, DOCX, ZIP | ImageIO, PDF context, hand-built DOCX |
| PDF | DOCX, JPG, PNG, TXT, ZIP | PDFKit (pages at 300 dpi; scanned PDFs become image DOCX) |
| Text / RTF / DOCX / HTML / Markdown | PDF, DOCX, TXT, RTF, HTML, ZIP | NSAttributedString, AppKit printing |
| Audio | MP3*, M4A, WAV, AIFF, FLAC, ZIP | AVFoundation, afconvert |
| Video | MP4, MOV, M4A, GIF*, MP3*, ZIP | AVFoundation, ffmpeg |
| SRT / VTT | TXT, SRT, VTT, ZIP | built in |
| ZIP / TAR / GZ / 7Z / RAR | Unzip into a folder | ditto, bsdtar, gzip |
| Folders, other files | ZIP | ditto / zip |

\* Needs `ffmpeg` or `cwebp` (Homebrew); hidden when not installed.

### 2.4 Tools wheel

Tools depend on the dragged files. Editors open in a small window that zooms
out of the drop point; saving writes a suffixed copy beside the original
("Name Cropped.jpg") and closes with a toast, so results can be dragged straight
back into a wheel. One-click tools run with the progress toast instead.

**Images:** Compress · Metadata · Edit · Annotate · Add BG · Crop · Redact ·
Read QR (+ Combine for several files)

- **Crop:** dimmed outside area, 8 handles, aspect presets (Free, 1:1, 16:9,
  9:16, 4:3, 3:4), live W × H fields in px. Reset / Apply.
- **Add BG:** gradient and solid swatches, custom image and colour; Padding,
  Corners and Shadow sliders in px; ratio Auto, 1:1, 16:9, 4:5. Saves PNG.
- **Annotate:** draw, arrow, box, highlight, text, numbered callouts; colours
  and stroke width; select, move, delete; full undo/redo.
- **Redact:** drag regions; black box, pixelate or blur; strips metadata;
  undo/redo.
- **Edit:** exposure, brightness, contrast, saturation, warmth, vibrance,
  sharpness, vignette; rotate and flip.
- **Compress:** quality or target size, JPEG/HEIC, max dimension; estimated
  result size; several images at once.
- **Metadata:** grouped EXIF/GPS/camera details with a location warning; remove
  all metadata into a clean copy; several images at once.
- **Read QR** (one click): decodes QR and other 2D codes and copies the text.

**PDFs:** Combine (PDFs and images, one click) · Pages (reorder, rotate,
delete) · Split (every page, every N pages, or ranges) · Compress (light,
balanced, strong) · Metadata (edit or remove).

**Video:** Trim · Speed (0.25×–4×, optional pitch keeping) · Crop · Compress
(resolution and quality) · Mute (one click). **Audio:** Trim · Speed.

## 3. Shelves

A shelf is a non-activating floating panel that holds references to files (it
never copies or modifies them by itself).

- **Create:** from the Actions wheel, or *New Shelf* in the menu-bar menu.
- **Add:** drop files anywhere on a shelf; duplicates are ignored.
- **Collapsed state:** a stacked preview of the last three thumbnails with an
  item-count badge. Dragging the stack drags every file.
- **Expanded state:** grid or list of all items. Toggle with the header button
  or double-click the stack.
- **Selection:** click, ⌘-click (toggle), ⇧-click (range), ⌘A. Dragging a
  selected item drags the whole selection. In an expanded shelf, dragging on
  empty space draws a selection rectangle: it selects what it touches, ⇧ adds
  to the selection and ⌘ toggles. Clicking empty space clears the selection.
- **Type-to-select:** typing letters while the shelf is key selects the first
  file whose name starts with them (case- and accent-insensitive) and scrolls to
  it; a pause of about a second starts a new prefix.
- **Drag out:** to Finder/any app. Plain drop follows the destination's rules
  (Finder: same volume moves, other volume copies, ⌥ forces copy). Files that no
  longer exist at their path afterwards are removed from the shelf. Optional
  setting: always remove dragged files from the shelf.
- **Keyboard (when the shelf is key):** Space = Quick Look, ⌫ = remove from
  shelf, Return = open, ⌘A = select all, Esc = collapse / close, ⌘W = close.
- **Context menu:** Open, Quick Look, Show in Finder, Copy, Copy Path, Share…,
  Remove from Shelf.
- **Edge docking:** release a shelf within 48 pt of the left or right screen edge
  and it docks flush to that edge (its outer corners square off). Shelves are
  always kept fully on screen.
- **Hiding at the edge:** once the pointer has stayed clear of a docked shelf for
  1.5 s (and no mouse button is down and Quick Look isn't open), the shelf slides
  behind the edge, leaving a 14 pt glass tab with the file count. Hovering or
  clicking the tab, or dragging files onto it, slides the shelf back out so the
  drop lands on it; adding files from the Actions wheel also reveals it. With
  Reduce Motion the shelf fades instead of sliding. Can be turned off in Settings.
- **Restoring:** open shelves (files, position, docked edge, expanded state and
  view mode) are saved to `~/Library/Application Support/OpenShelf/shelves.json`
  shortly after each change and on quit, and reopened at launch. Each file is
  saved with its path and a bookmark, so files moved or renamed in the meantime
  are still found; files that are gone are skipped, and a shelf left empty isn't
  reopened. Can be turned off in Settings.
- **Close:** header × button; the shelf also closes when its last item is
  removed. Closing never touches the files.
- Multiple shelves may exist at once.

## 4. Menu-bar item

New Shelf · Close All Shelves · Settings… · Quit.

## 5. Settings

- Trigger keys per wheel (any combination of ⌃ ⌥ ⇧ ⌘, at least one).
- Remove files from a shelf after dragging them out (default off).
- Restore shelves when OpenShelf opens (default on).
- Hide docked shelves to the screen edge (default on).
- Open at login.

## 6. Roadmap

1. ~~Convert wheel~~ (done, §2.3) — wedges chosen by input type (e.g. PDF → DOCX/JPG/PNG/TXT;
   image → PNG/WEBP/HEIC/TIFF/AVIF/BMP/PDF/DOCX). Output is saved beside the
   original as a new file and never overwrites (`Name.jpg`, `Name 2.jpg`, …).
   Images via ImageIO/Core Image, audio/video via AVFoundation (ffmpeg optional
   for formats Apple frameworks lack), PDF via PDFKit, text/RTF/DOCX via
   NSAttributedString.
2. ~~Tools wheel~~ (done, §2.4) — hub shows the file size until a wedge is hovered. Tools:
   compress (quality or target size), crop, metadata view/strip, redact,
   annotate, add background, edit (adjustments), trim/split video & audio,
   PDF merge/split/reorder, QR decode. Tools that need input open a small
   editor window, and save a suffixed copy (`Name Cropped.jpg`), so results can
   be chained:
   - *Crop:* aspect presets (Free, 1:1, 16:9, 9:16, 4:3, 3:4), exact W×H in px,
     drag handles, Reset / Apply.
   - *Add background:* gradient and solid swatches or a custom image; padding,
     corner radius and shadow sliders; output ratio (Auto, 1:1, 16:9, 4:5).
3. ~~Keyboard control of an open wheel (arrows + Return).~~ (done, §2)
4. PDF redaction and video/audio tools beyond trim, speed, crop, compress and mute.
5. ~~Shelf auto-hide: docked shelves slide into a slim tab and reveal on hover.~~ (done, §3)
6. ~~Rubber-band selection and type-to-select in expanded shelves.~~ (done, §3)
7. ~~Shelf persistence across relaunches (with bookmarks).~~ (done, §3)
8. ~~Batch progress for long-running conversions.~~ (done: the progress card, §2.3)
9. Notarized builds once a Developer ID is available; then a Homebrew cask.
