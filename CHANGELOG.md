# Changelog

All notable changes to OpenShelf. The newest version at the top is released
automatically: push it to `main` and the Release workflow builds the app,
tags `v<version>` and publishes a GitHub release with that section as notes.

Format: one `## [x.y.z] - YYYY-MM-DD` heading per version, newest first.

## [1.2.0] - 2026-10-21

Extra formats in one click, tidier wheels, and Settings that fits on screen.

### New

- **Install extra formats from Settings.** The new **Extra Formats** tab
  installs ffmpeg (MP3, OGG, WebP, MKV, WebM, AVI, WMV) and LibreOffice
  (PowerPoint, ODP, XLS, ODS) for you: no Terminal, no Homebrew, no restart.
  Every download is checked against its developer's Apple signature before
  it runs. If the download server is down, OpenShelf uses its own backup copy
  of the same signed files, or Homebrew if you have it. Nothing is downloaded
  unless you click Install.
- **Wheels show six at a time.** Busy wheels (Tools for an image, Convert for
  images, PDFs and video) now show the six everyday options plus **⋯ More**.
  Rest on More for a moment to see the rest; **Back** sits in the same spot.
  Arrow keys, Return and clicks work too.
- **Video → GIF without ffmpeg.** GIFs are now made by macOS itself, so every
  Mac gets the GIF wedge for videos.
- **Videos macOS can't read still work** when ffmpeg is installed, such as a
  TV-stream (`.ts`) recording saved as `.mp4`. Converting and the video tools
  fall back to ffmpeg with progress and Cancel, and never leave a half-written
  file. Thanks to [@Lwuisyy](https://github.com/Lwuisyy) for the idea in
  [#2](https://github.com/Manan-Santoki/OpenShelf/pull/2).

### Improved

- **Settings has tabs:** General, Wheels, Shelves, Extra Formats and Updates,
  each sized to its contents.
- **Clear messages when a tool is missing.** Dragging an MKV without ffmpeg
  says "MKV needs ffmpeg" instead of showing a lone ZIP wedge.
- **WebP is offered only when it will work.** Some ffmpeg builds can't write
  WebP; OpenShelf now checks first. Thanks again to @Lwuisyy.
- The Markdown wedge is labelled **MD**, like the other formats.
- The welcome guide links straight to Extra Formats.

## [1.1.0] - 2026-10-21

The biggest update yet: many more formats, five new tools, and the wheel
without dragging.

### New

- **Open the wheel without dragging.** Select files in Finder and press
  **⌃⌥Space**: arrow keys choose, Tab switches between Convert, Tools and
  Actions, Return runs it, Esc closes. Change or turn off the shortcut in
  Settings → Keyboard. (macOS asks once for permission to read Finder's
  selection.)
- **New tools**
  - **Resize** images, one or many: exact size, percentage or presets, without
    accidental upscaling.
  - **Remove Background** from photos of people, pets and objects, saved as a
    transparent PNG.
  - **Merge** several videos into one MP4, or several audio files into one M4A.
  - **Split** a video or audio file into equal parts, every N seconds, or at
    the times you choose.
  - **Rotate** videos left, right, 180° (without re-encoding) or flip them.
- **New conversions**
  - Audio: **OGG, Opus, WMA**, and OGG/Opus/WMA/MKA files as input.
  - Video: **MKV, WebM, AVI, WMV**, and those (plus FLV) as input.
  - PDF → **PowerPoint, Excel, EPUB, Markdown**.
  - Documents → **EPUB, Markdown**; RTFD documents as input.
  - **Spreadsheets:** CSV ⇄ TSV ⇄ Excel (XLSX), and tables to PDF.
  - **Slides:** PowerPoint (PPT/PPTX/ODP) → PDF, JPG, PNG, and Excel/ODS
    conversions, when LibreOffice is installed.
  - Archives: **TAR** and **TAR.GZ** alongside ZIP.

### Fixed

- **The wheel opens every time.** After OpenShelf had sat idle for a while,
  macOS slowed it down to save energy, and a drag could go unnoticed until a
  shelf was opened ([#1](https://github.com/Manan-Santoki/OpenShelf/issues/1)).
  OpenShelf now wakes the moment you press the mouse button, follows the drag
  closely, and goes back to sleep as soon as you let go, so it's both reliable
  and lighter on battery than before.
- **Quick taps work.** A brisk tap of ⇧ (or ⇧⌥, ⌃) used to be too short to
  count. Now the wheel opens as soon as you let go of the key; holding the keys
  still works as before.
- **Return in the wheel stays in the wheel.** With a shelf open, pressing
  Return to run a wedge could also open the shelf's files, and the wheel stayed
  up to run a second action on release. Wheel keys no longer reach shelves, and
  the wheel closes as soon as Return runs something.
- **Video Compress keeps its promises.** 720p is now 1280×720 and 480p is
  854×480 (not 960×540 or 224×128), Quality sets a real bitrate, the size
  estimate matches the result, and a video that wouldn't get smaller isn't
  saved. HDR, rotation, date and location are kept.
- **Crop saves exactly the size you type** (800 × 450 was coming out 801 × 451).
- **Annotate takes your text straight away**, without a second click, and text
  is a readable size on large photos.
- **The keyboard wheel always starts on Convert.** Letting go of ⌥ before ⌃
  after ⌃⌥Space could switch it to Actions.
- **Settings comes to the front** every time you choose it, and fits on
  smaller screens.
- **Docking a shelf keeps it where you dropped it**, instead of moving it to
  the middle of the screen edge.
- Trim's length now matches the start and end it shows.

### Improved

- **The wheel feels snappier.** It appears in about a tenth of a second, the
  first time too, and blooms open faster.
- **Busy wheels are easier to read.** Labels fit their wedge on wheels with
  10 or more options, and the size or action shows once, above the wheel.
- **Files dropped on a shelf appear at once**, with thumbnails filling in after.
- **Results say what happened:** "1 resized · 2 already smaller", "Nothing
  saved" when files couldn't shrink, and Save stays disabled until there's a
  change to save.
- **Redact each area your own way:** a black box over a name and a blur over a
  face in the same image.
- **Tidier output:**
  - ZIPs leave out Mac-only `__MACOSX` and `.DS_Store` files.
  - Unzipping a single folder puts it right next to the archive.
  - Names don't repeat ("Photo Compressed 2", not "Photo Compressed Compressed").
  - Lossy audio is sized to its source (a voice memo to OGG is now about 20 KB,
    not 110 KB).
- **Windows behave:** drag shelves and editors by their titles, Tab moves
  between fields, editor layouts don't jump when a message appears, and the
  Colors panel opens beside the editor.
- Cancelling a video or audio export never leaves a half-written file behind.
- Tools aren't offered for MKV, WebM, AVI and other formats they can't edit;
  you're told how to convert them first.

Audio/video formats beyond Apple's own need `brew install ffmpeg`; slides and
ODS need `brew install --cask libreoffice`. Those wedges stay hidden until the
tool is installed.

## [1.0.4] - 2026-10-21

### Fixed

- **The wheel's shadow is no longer cut off.** Its soft shadow used to stop at a
  straight edge around the wheel, which showed on dark backgrounds. The wheel
  now has room for the whole shadow, so it blends in seamlessly. Thanks to the
  Reddit commenter who spotted it.
- **You can always see what a wedge will do.** Finder's drag thumbnail covers
  the centre of the wheel, so the file size and the hovered action now also
  appear in a label above the wheel.
- **Arrow keys no longer switch desktops by accident.** Control or Command plus
  an arrow is a macOS shortcut (⌃→ switches desktops), so the wheel's arrow keys
  wait until those keys are released, and the label says so.

## [1.0.3] - 2026-10-20

Fixes from a full edge-case test run (724 checks across 60+ unusual files).

### Privacy

- **Metadata removal now always removes location.** HEIC, HEIF and AVIF
  photos (the iPhone's formats) could keep their GPS location. Every cleaned
  copy is now re-checked, and nothing is saved if anything identifying is left.

### Fixed

- Password-protected PDFs no longer convert to blank pages; you're told to
  remove the password first.
- Scanned PDFs (no text layer) no longer produce empty TXT files.
- Text files in older encodings (Windows/Latin-1, UTF-16) now convert.
- SVG files now work with every conversion and tool.
- Silent videos aren't offered MP3 or M4A, which they can't produce.
- Converting very large documents no longer freezes the app.
- **Compress** keeps PNGs as PNG, keeps transparency, says when a format has to
  change (e.g. GIF animation), uses `.jpg` instead of `.jpeg`, and never saves a
  "compressed" copy that's bigger. The same goes for PDF compression.
- Clear messages instead of technical ones: password-protected or damaged
  archives, and saving into a folder you can't write to, among others.
- Very long file names no longer fail when a suffix like "Cropped" is added.
- Symlinks and files without an extension are recognised by their contents.
- Files whose names start with a dot no longer produce hidden results.

## [1.0.2] - 2026-10-20

### New

- **Automatic updates.** OpenShelf now checks GitHub for new versions once a
  day (it asks first) and installs them in place: you'll see the release notes
  and an Install Update button, and the app restarts on the new version. Use
  **Check for Updates…** in the menu bar, or the new **Updates** section in
  Settings. Updates are signed, so only genuine OpenShelf releases install.

This is the last version you need to download by hand; later versions arrive
through the app.

## [1.0.1] - 2026-10-20

### New

- **Show Shelves** in the menu-bar menu brings every shelf to the front and
  slides out any that are tucked at a screen edge. Open shelves are also listed
  by name, so you can jump to one. A shelf you asked to see stays out until
  your pointer has been over it.

### Fixed

- Shelves couldn't be moved. Drag a shelf by its top bar again to move it or
  dock it at a screen edge.

## [1.0.0] - 2026-10-20

The first stable release.

### New

- **Wheel keyboard control.** While a wheel is open mid-drag, the arrow keys
  move the highlight, Return runs it and Esc cancels. Your mouse release
  afterwards is caught, so the files don't also land wherever the pointer is.
- **Shelves remember themselves.** Open shelves come back after a relaunch,
  including files that were moved or renamed in the meantime (Settings →
  "Restore shelves when OpenShelf opens").
- **Docked shelves hide at the screen edge**, leaving a slim tab with the file
  count. Hover it, click it or drag files onto it to slide the shelf back out
  (Settings → "Hide docked shelves to the screen edge").
- **Rubber-band selection** in expanded shelves: drag on empty space to select.
- **Type-to-select** in shelves: type the start of a file name.
- Screenshots and a full "How to use" guide in the README.

### Fixed

- Adding files to a shelf could crash the app while the shelf was resizing.
- Slowing down an audio-only file with "Keep audio pitch" off could hang.

## [0.9.0] - 2026-10-20

The first public release.

- **⇧ Convert wheel:** images, PDFs, documents, audio, video, subtitles and
  archives into the formats that fit them, saved next to the original with a
  cancellable progress card.
- **⇧⌥ Tools wheel:** image editors (Compress, Metadata, Edit, Annotate, Add
  Background, Crop, Redact, Read QR), PDF tools (Combine, Pages, Split,
  Compress, Metadata) and video/audio tools (Trim, Speed, Crop, Compress, Mute).
- **⌃ Actions wheel:** Shelf, Add to Shelf, AirDrop, Copy, Move, Copy Path,
  Terminal.
- Floating shelves, the Monoline app icon and menu-bar glyph, a welcome guide,
  and a drag-to-Applications installer.
