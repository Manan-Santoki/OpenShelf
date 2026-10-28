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
- Following starts on any primary-button press (or, if that press was never
  seen, on its first drag movement; a once-a-second check catches the rest).
  The "before" change count is the one from after the previous press, kept
  current once a second while the button is up, so a pasteboard written a
  little after the button goes down still counts and one written just after
  it comes up doesn't.

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

- Without a drag: a global shortcut (default ⌃⌥Space, set or turned off in
  Settings → Wheels; registered with Carbon hot keys, so no Accessibility
  permission) opens the Convert wheel at the pointer for the files selected in
  Finder (read over Apple Events, which asks for Automation permission once), or
  the focused shelf. If OpenShelf is frontmost with no focused shelf selection,
  the most recently used shelf’s selection is used before Finder.
  Holding Command shows number badges: ⌘1–⌘7 run the numbered wedge, ⌘0
  opens More or Back; numbers restart on each page. ⌘⌥number requests output
  options. Other Command shortcuts remain available to the app.
  Arrows move the highlight (starting on the first wedge), Return runs it,
  clicking a wedge runs it, ⌥Return or ⌥-click opens a format's output options
  (§2.3.1; keys still held from the shortcut itself don't count), Tab / ⇧Tab or
  tapping a wheel's trigger keys switches wheels (the label above names the
  wheel), Esc or clicking elsewhere closes. On a Convert wheel with options the
  opening hint reads "Return runs · ⌥Return for options · Tab switches wheel"
  to fit above the wheel. Starting a drag closes it. If no shelf selection is
  available and Finder is not in front or has no selection, a toast says
  "Select files in Finder first".

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
| Image (incl. SVG) | PNG, JPG, WEBP†, HEIC, TIFF, AVIF, BMP, PDF, DOCX, ZIP | ImageIO, PDF context, hand-built DOCX |
| PDF | DOCX, JPG, PNG, TXT, MD, EPUB, PPTX, XLSX, ZIP | PDFKit (pages at 300 dpi; scanned PDFs become image DOCX/EPUB/PPTX; TXT/MD/XLSX refuse scans); hand-built EPUB 3, PresentationML (one picture per slide) and SpreadsheetML (one sheet per page, cells split at tabs/wide gaps) |
| Text / RTF / RTFD / DOCX / DOC / ODT / HTML | PDF, DOCX, TXT, MD, EPUB, HTML, RTF, PAGES¶, ZIP | NSAttributedString, AppKit printing, Core Text; Markdown and EPUB from detected headings, lists, bold/italic and links. DOCX/DOC/ODT/RTF → PDF through LibreOffice‡ when installed; DOCX/ODT/HTML → MD and EPUB through Pandoc§ |
| Markdown | PDF, DOCX, TXT, EPUB, HTML, RTF, ZIP | Built-in parser (CommonMark, GitHub tables/task lists/strikethrough, Pandoc footnotes, definition lists, simple and multi-line tables, line blocks, math, smart punctuation, YAML title) rendered to AppKit rich text (PDF, RTF), hand-built WordprocessingML with Word styles, numbering, tables, footnotes and pictures (DOCX), XHTML (HTML, EPUB) and plain text (TXT). Pandoc§ for DOCX/HTML/EPUB/RTF, and PDF when it has a typesetter |
| CSV / TSV / XLSX | XLSX, CSV, TSV, PDF, ODS‡, NUMBERS¶, ZIP | Built-in RFC 4180 parser, SpreadsheetML writer/reader, Core Text table PDF (landscape, repeated header) |
| XLS / ODS | XLSX, CSV, PDF, ODS‡, NUMBERS¶ (XLS), ZIP | LibreOffice‡ |
| PPTX / PPT / ODP | PDF‡, JPG‡, PNG‡, KEY¶ (PPTX, PPT), ZIP | LibreOffice‡ → PDF → page images ("Name Slide 2.jpg") |
| Pages | PDF, DOCX‡, RTF‡, TXT‡, JPG, PNG, ZIP | LibreOffice‡ (libetonyek), → PDF → page images; without it, the Quick Look preview inside the package (see below) |
| Numbers | PDF, XLSX‡, CSV‡, ODS‡, JPG, PNG, ZIP | as Pages |
| Keynote | PDF, PPTX‡, JPG, PNG, ZIP | as Pages; images are "Name Slide 2.jpg" |
| Audio | MP3†, M4A, WAV, AIFF, FLAC, OGG†, OPUS†, WMA†, ZIP | AVFoundation, afconvert; ffmpeg† for MP3/OGG/Opus/WMA and for sources macOS can't decode (OGG, Opus, WMA, MKA, APE, AC3) |
| Video | MP4, MOV, M4A, GIF†, MP3†, MKV†, WEBM†, AVI†, WMV†, ZIP | AVFoundation; ffmpeg† (H.264/AAC, VP9/Opus, MPEG-4/MP3, WMV2/WMA2; MKV copies streams when it can) and for sources macOS can't open (MKV, WebM, AVI, WMV, FLV, MPEG, …) |
| SRT / VTT | TXT, SRT, VTT, ZIP | built in |
| ZIP / TAR / GZ / 7Z / RAR | Unzip into a folder | ditto, bsdtar, gzip |
| Folders, other files | ZIP, TAR, TAR.GZ | ditto / zip / bsdtar |

† Needs `ffmpeg` (or `cwebp` for WebP) from Homebrew; hidden when not installed.
OGG uses Vorbis when ffmpeg has libvorbis, otherwise Opus in an Ogg container.
Silent videos never offer audio outputs (MP4/MOV checked directly, other
containers with ffprobe).
‡ Needs LibreOffice (`brew install --cask libreoffice`), run headless with a
private profile and a 120-second limit; hidden when not installed.

§ Pandoc is used when found (Homebrew, MacPorts, PATH, or OpenShelf's own copy
from Settings → Extra Formats: the newest GitHub release's zip for this Mac,
checked against the SHA-256 digest GitHub lists and the Developer ID of team
5U2WKE6DES), with a 180-second limit. Its PDFs need a typesetter, found in the
same places and `/Library/TeX/texbin`: XeLaTeX, LuaLaTeX, Tectonic, Typst,
pdfLaTeX, WeasyPrint or wkhtmltopdf, in that order. If Pandoc or LibreOffice
fails, the built-in conversion runs instead.

Pages, Numbers and Keynote files (`IWorkConversion`) are zip files, or folder
packages in older and synced copies; `FileKind` classifies both by extension
as document, spreadsheet and presentation. With LibreOffice they convert
through it, falling back to the preview if it fails. Without it, the preview
saved inside for Quick Look is used: `QuickLook/Preview.pdf` (iWork '09: every
page) gives PDF, JPG and PNG; `preview.jpg` (current versions) or
`QuickLook/Thumbnail.jpg` gives JPG and PNG of the first page. Zip entries are
found by reading the central directory (`ZipDirectory`), so the wheel never
unpacks anything; the preview is extracted with `unzip -p` when converting.
With no preview and no LibreOffice the wheel is ZIP-only, so the "Keynote
needs LibreOffice" toast shows instead, and a conversion that still runs says
to install it in Settings → Extra Formats.

¶ PAGES, NUMBERS and KEY (`IWorkExport`) are offered only when Pages, Numbers
or Keynote is in /Applications or ~/Applications (checked by bundle identifier,
`com.apple.iWork.Pages` etc.), for files that app opens: DOCX, DOC, RTF, RTFD
and TXT for Pages; XLSX, XLS, CSV and TSV for Numbers (on spreadsheet wheels);
PPTX and PPT for Keynote (on slide wheels, even without LibreOffice). The app
does the work over Apple Events: `osascript` opens the file, `save … in` writes
the new document next to the original, then it's closed and the app quit if
it wasn't already running (300-second limit). Paths are passed as script
arguments, never spliced into the script. Denied Automation access (-1743)
says to allow it in Privacy & Security → Automation.
Wheels hold at most 12 wedges (the largest, images, has 10).

#### 2.3.1 Output settings

Settings → Output (§5) chooses how conversions encode; each batch takes a copy
when it starts. Every setting defaults to Automatic, which converts exactly as
before: MP4/MOV through AVFoundation's Highest Quality preset (or an ffmpeg
stream copy), M4A through the Apple M4A preset, ffmpeg's x264 at CRF 20, VP9 at
CRF 32, MPEG-4/WMV2 at q 4, lossy audio at `AudioBitRate`'s rate, JPEG/HEIC/
AVIF at 0.9 with metadata (WebP 88, PDF pages 0.92), GIFs 720 px wide at 12 fps.

| Setting | Native (AVFoundation) | ffmpeg |
|---|---|---|
| Video codec: Automatic / H.264 / HEVC (MP4, MOV, MKV) | Any video or fixed audio setting re-encodes MP4/MOV like Compress (AVAssetReader/Writer, `VideoCompressionPlan`); Automatic is H.264. HDR always stays 10-bit HEVC | libx264; HEVC with libx265, else `hevc_videotoolbox` (falling back to the next encoder if it fails), else libx264; `-tag:v hvc1` in MP4/MOV. HDR keeps HEVC once any video setting is chosen |
| Quality: Automatic / Best / Balanced / Smaller / Custom Mb/s | 1.5×, 2×, 1× and 0.5× Compress's High bitrate; Automatic and Best capped at the source's own rate, Balanced and Smaller at 80% of it; Custom is used as is | x264 CRF 20/18/23/28 (x265 +5; VideoToolbox q 65/75/55/40); VP9 CRF 32/28/33/38; MPEG-4/WMV2 q 4/2/5/8; Custom is `-b:v` (with `-maxrate` 1.5× and `-bufsize` 2× for H.264/HEVC) |
| Largest size: Original / 4K / 1080p / 720p / 480p | Fitted inside 3840×2160, 1920×1080, 1280×720 or 854×480 either way round, never enlarged, even sides | The same as a `scale` filter |
| Audio bitrate: Automatic / 96–320 kb/s | AAC at that rate for every sound track (mono at most 256 kb/s); M4A is written with AVAssetWriter instead of the preset | `-b:a` for MP3, AAC, Opus, Vorbis and WMA (mono Opus at most 256, mono Vorbis 192) |
| Image quality: Automatic / 40–100% | JPEG, HEIC, AVIF; PDF pages as JPEG | cwebp `-q`, ffmpeg `-quality` for WebP |
| Keep metadata (on) | Off writes ImageIO formats without the source's properties | — |
| GIF width 480 / 720 / 1080 / original up to 1920 px; 10 / 12 / 15 / 24 fps | `VideoGIF.plan` | `fps=…,scale='min(…,iw)':-2` |

Stream copies (MP4/MOV remux, MKV copy-all) are skipped when video or audio
settings ask for a re-encode.

Holding ⌥ as a format's wedge runs (drop, click or Return) opens a small window
first, for lossy outputs only: MP4, MOV, MKV, WebM, AVI, WMV (video and audio
rows; the codec only for MP4/MOV/MKV), M4A, MP3, OGG, Opus, WMA (audio), JPEG,
HEIC, AVIF (quality and metadata; JPEG from PDF pages, quality only), WebP
(quality) and GIF from video (width, frame rate). It's prefilled from Settings
→ Output; video shows an estimated size (summed over the files, "Estimating…"
while they're read). Convert uses the choices once; "Use as default" also
saves the rows shown, leaving the rest of Settings → Output as it was. Other
formats convert at once.

### 2.4 Tools wheel

Tools depend on the dragged files. Editors open in a small window that zooms
out of the drop point; saving writes a suffixed copy beside the original
("Name Cropped.jpg") and closes with a toast, so results can be dragged straight
back into a wheel. One-click tools run with the progress toast instead.

Every editor takes several files. With one file it looks exactly as it does
alone; with several, a filmstrip under the canvas ("2 of 5 · name", thumbnails,
‹ ›, a dot on files with an edit waiting) moves between them, and the button
counts the files that would change ("Crop 3 Images"); untouched files are left
alone and counted in the closing toast ("2 cropped · 1 not cropped").

- **Same for all** applies one edit to every file; turning it on copies the
  shown file's settings to the rest, off lets each keep its own. On by default
  for Crop, Edit, Add BG, video Crop, Rotate and PDF Split; off by default for
  Trim and Split (clips differ in length). Crops are kept as fractions of each
  file's size, so "the top half" stays the top half on files of any size.
- **Annotate, Redact, Remove BG and PDF Pages** have no switch: marks, cutouts
  and page edits belong to one file, and each keeps its own undo history.
- With Same for all, Trim cuts the same seconds from the start and end of each
  file, Split uses the same times or parts where they fall inside a file, and
  PDF Split reads ranges against the longest PDF and cuts them to each one.

**OCR (images and PDFs, including mixed selections):** choose Searchable PDF
or Text. Vision recognition stays on this Mac and uses supported preferred system
languages. PDF pages with existing text keep their content; scanned pages are
rendered and released individually. Closing cancels between recognition operations;
a cancelled or failed PDF save leaves no partial output. Copies are saved next to
the originals with an OCR suffix. No extra tools or permissions are required.

**Images:** Compress · Metadata · Edit · Annotate · Add BG · Crop · Resize ·
Remove BG · Redact · Read QR · OCR (+ Combine for several files)

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
- **Resize:** by percent, longest side (presets 1080, 1920, 3840 px) or exact
  W × H with proportions locked or free; never enlarges unless "Allow
  enlarging" is on; Lanczos scaling; same format, metadata kept with the new
  dimensions; several images at once ("Name Resized.jpg").
- **Remove BG:** Vision foreground segmentation cuts out the subject(s);
  before/after preview on a checkerboard; saves a transparent PNG ("Name No
  Background.png"); explains when no clear subject is found.
- **Read QR** (one click): decodes QR and other 2D codes and copies the text.

**PDFs:** Combine (PDFs and images, one click) · Pages (reorder, rotate,
delete) · Split (every page, every N pages, or ranges) · Compress (light,
balanced, strong) · Metadata (edit or remove).

**Video:** Trim · Speed (0.25×–4×, optional pitch keeping) · Crop · Rotate
(left, right, 180° without re-encoding; flip re-encodes) · Split · Compress
(resolution and quality) · Mute (one click). **Audio:** Trim · Speed · Split.

- **Split:** into 2–20 equal parts, every N seconds or minutes, or at chosen
  times ("0:30, 1:15"); shows the parts before exporting; each part is
  exported without re-encoding where the container allows ("Name Part 1.mp4").
- **Merge** (several videos, or several audio files): reorder by dragging,
  remove entries; videos join into "Merged.mp4" at the first clip's size, with
  other shapes letterboxed and turned upright; audio joins into "Merged.m4a".

Media exports are written to a hidden file and moved into place only when
complete, so a cancelled or failed export never leaves a partial file.

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

### 3.1 Watched folders

Off until a folder is added in Settings → Shelves → Watched Folders (Add
Folder… with an open panel, or Watch Screenshots: the `location` default of
`com.apple.screencapture`, with `~` expanded, else `~/Desktop`; re-read each
time watching starts). Saved to
`~/Library/Application Support/OpenShelf/watched-folders.json` with a path and
a bookmark per folder, a per-folder on/off switch and an optional action.

- **Watching:** a dispatch source per switched-on folder (top level only)
  says when its entries change; the folder is then listed off the main thread.
  Files present at the first listing are never reported. A file renamed inside
  the folder (same file number) or saved over in place under the same name
  isn't new. Hidden names (`.DS_Store`, Screenshot's `.Screenshot …` partials),
  `~$` lock files and `.download`, `.crdownload`, `.part`, `.partial`,
  `.opdownload`, `.tmp`, `.temp`, `.screenshot` files are ignored, so the
  finished file they're renamed to counts as new. Only regular files count.
- **Debounce:** a new file is reported once its size has stayed the same for
  1 s (5 s for an empty file) and Finder no longer marks it as copying
  (creation date 1984-01-24); the folder is listed every 0.5 s meanwhile.
- **Routing:** new files go on the folder's own shelf (revealed if hidden at
  an edge), or a new shelf near the pointer when it has none open.
- **Action:** none, a conversion (JPG, PNG, HEIC, WebP, AVIF, PDF, MP4, GIF,
  M4A, MP3) or a single-file tool, run through the same paths as the wheels
  on the files it applies to, with the shelf as the source shelf (so results
  follow the results-back-on-the-shelf setting). Files OpenShelf wrote itself
  are recorded as each one is saved and never count as arrivals.
- A folder that's deleted, moved away or ejected stops being watched and
  shows "Folder not found"; switching it off and on tries again.

## 4. Menu-bar item

New Shelf · Show Shelves · Close All Shelves · Open Wheel for Selection ·
Take the Tour… · Settings… · Check for Updates… · Quit.

When the icon can't be reached (behind the notch, or in the macOS 27 menu
bar's overflow), opening the app again from Finder or Spotlight shows
Settings, and so does pressing the wheel shortcut again while "Select files in
Finder first" is showing (within 2 s; that toast says so). Settings → General
mentions both.

## 5. Settings

Toolbar tabs, like System apps; the window keeps its width, takes each tab's
height (scrolling when the screen is shorter) and opens on the tab used last.

- General: open at login; take the tour, or start it over (§6).
- Wheels: trigger keys per wheel (any combination of ⌃ ⌥ ⇧ ⌘, at least one),
  and the shortcut for Finder's selection.
- Shelves: remove files from a shelf after dragging them out (default off);
  restore shelves when OpenShelf opens (default on); hide docked shelves to the
  screen edge (default on); add conversion and tool results back to the shelf
  (default on); watched folders (§3.1, none by default).
- Output: video codec, quality (or a custom bitrate) and largest size; audio
  bitrate; image quality and metadata; GIF width and frame rate (§2.3.1), all
  Automatic by default, with Reset to Automatic. Stored as one JSON value;
  anything unreadable falls back to Automatic, setting by setting.
- Extra Formats: install or remove ffmpeg, LibreOffice and Pandoc. ffmpeg comes from
  its build server, then OpenShelf's GitHub mirror of the same signed zips;
  Pandoc from its GitHub releases;
  when every download source is down, Install with Homebrew is offered if
  Homebrew is installed. A failed checksum or signature never falls through
  to another source.
- Updates: automatic checks and installs.

## 6. Guided tour

Opens by itself once, on first launch, and on the first launch after updating
for people who saw the earlier welcome guide (titled "New: Learn OpenShelf
Hands-On" for them); after that from the menu bar and Settings → General. A window with the steps listed on the left and the current one on
the right: Welcome, then eight practice steps (Convert a photo, Markdown to
PDF, Edit with Tools, Work with PDFs, Trim a video, Keep files on a shelf, No
drag needed, Choose the settings), then You're set.

- **Practice files** are drawn and encoded at runtime (the app carries no
  samples) into `~/Library/Application Support/OpenShelf/Practice`: a JPEG
  landscape, Markdown notes with a table, a three-page PDF, a four-second
  H.264 clip and a plain-text letter. Results are saved beside them, as
  anywhere else, and listed under the step ("Made so far"). The last page
  moves the folder to the Trash on request.
- **Each step** names the file, shows the wheel's keys as set in Settings
  (as keycaps), and the wedge to drop on. The practice file is a real file
  drag source, so the wheels open exactly as they do from Finder.
- **Progress** comes from the app itself while the tour is open: a wheel
  opening for the step's wheel marks it under way ("The Convert wheel is
  open. Now drop on PNG."); running the right kind of wedge on the right kind
  of file completes it. Any file of the right kind counts. Done steps are
  remembered across launches; Skip for Now moves on without them.

## 7. Shortcuts and Spotlight

App Intents (`Sources/OpenShelf/Intents`) expose the engines with no UI, run
in the app's process (`openAppWhenRun` is false) and never go online.

- **Actions:** Convert Files (files + format; every `OutputFormat`, as the
  `IntentFormat` choice), Compress Images and PDFs (Light/Balanced/Strong:
  the PDF tool's levels, image quality 85/70/45), Extract Text (text file or
  searchable PDF), Remove Background, Combine into PDF, Remove Audio from
  Videos, Read QR Codes (returns strings) and Add to Shelf (most recent shelf,
  or a new one). File actions return the files written.
- **Inputs:** each file must be a kind the matching Tools-wheel entry takes.
  Convert accepts only formats `ConversionCatalog` offers for the file; a file
  already in the target format is passed on unchanged, ZIP/TAR/TAR.GZ make one
  archive of all the files. Compress passes on a file that wouldn't get
  smaller. A failing file doesn't stop the rest, but the action then fails
  naming it ("“a.png”: reason (and 2 more files failed)").
- **Where results go:** next to each file, as on the wheels. Files with no
  location on disk, or in a folder that can't be written to, are copied to a
  temporary folder first; Add to Shelf copies such files to
  `~/Library/Application Support/OpenShelf/From Shortcuts` instead, so the
  shelf's references outlast the shortcut.
- **App Shortcuts** (Spotlight on macOS 26+, Siri, the Shortcuts gallery):
  Convert Files, Compress, Extract Text, Remove Background, Combine into PDF,
  Add to Shelf.
- **Build:** `make` runs `scripts/appintents-metadata.sh`, which compiles the
  sources once more for their constant values (`-emit-const-values-path`) and
  runs Xcode's `appintentsmetadataprocessor` to write
  `Contents/Resources/Metadata.appintents`. The processor only handles apps
  whose linker dependency info lists AppIntents.framework, so the app is
  linked with `-framework AppIntents -Xlinker -dependency_info` and that file
  is passed along. Without that tool it warns and the
  app builds without the actions.

## 8. Roadmap

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
10. Follow AppKit's move to gesture recognizers (TN3212): wheel and shelf
    mouse tracking still uses mouse events and tracking areas, which macOS 27
    supports; port it to `NSGestureRecognizer` when that becomes the better path.
