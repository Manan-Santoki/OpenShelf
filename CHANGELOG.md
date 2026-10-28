# Changelog

All notable changes to OpenShelf. The newest version at the top is released
automatically: push it to `main` and the Release workflow builds the app,
tags `v<version>` and publishes a GitHub release with that section as notes.

Format: one `## [x.y.z] - YYYY-MM-DD` heading per version, newest first.

## [Unreleased]

### Fixed and improved

- **Dragging with the wheel open is smooth again.** Two things on the wheel's
  hover path were doing far more work than a mouse move needs:
  - Once a wheel appeared mid-drag, moving the pointer over it re-read the
    system drag pasteboard on every single mouse move instead of once when
    the drag arrived. The dragged files are now read once and reused for the
    rest of the hover, matching how shelves already read drops.
  - Tracking the highlight and the More/Back dwell ring wrote to the wheel's
    `@Observable` state on every hover reading even when the value hadn't
    changed, which re-renders every petal regardless. Those writes now happen
    only when the value actually changes.

## [1.7.0] - 2026-10-25

A hands-on tour, and Pandoc in one click.

### New

- **A hands-on tour.** The first time OpenShelf opens, a guided tour teaches
  it by doing: practice files are made for you (a photo, Markdown notes, a PDF,
  a video and a letter), and each step shows the keys to press and waits until
  you've done it for real, from converting a photo and typesetting Markdown to
  trimming a video, using shelves and opening a wheel without dragging. Skip any
  step, or the whole tour; take it again or start over from the menu bar or
  Settings → General. It replaces the welcome window, and opens once after
  this update too, so you can try it (or choose Not Now).
- **Install Pandoc from Settings.** Settings → Extra Formats installs Pandoc
  in one click, like ffmpeg and LibreOffice: the official build from GitHub,
  checked against GitHub's checksum and Pandoc's code signature, installed
  for you alone with no administrator password. Remove it there too.

## [1.6.0] - 2026-10-25

Markdown that converts properly, and Pandoc when you have it.

### New

- **Pandoc, when it's installed.** With [Pandoc](https://pandoc.org)
  (`brew install pandoc`), Markdown becomes Word, HTML, EPUB and RTF through it,
  and Word, ODT and HTML documents become Markdown and EPUB. PDFs go through
  Pandoc too when a typesetter such as MacTeX, Tectonic or Typst is installed.
  Settings → Extra Formats shows whether it was found. If Pandoc fails, the
  built-in conversion runs instead.

### Fixed and improved

- **Markdown converts properly.** Markdown to PDF, Word, HTML, RTF and plain
  text used to come out as the Markdown source itself, asterisks and all
  ([#5](https://github.com/Manan-Santoki/OpenShelf/issues/5)). Headings,
  emphasis, lists, quotes, code blocks, tables, links, pictures, footnotes and
  math are now the real thing in every format, and Word files use Word's own
  heading styles, numbered lists and footnotes. Pandoc's definition lists,
  simple and multi-line tables, line blocks, math and smart quotes are read
  too. Thanks to [@wada3n](https://github.com/wada3n) for the report and the
  comparison files.
- **Word documents to PDF keep their layout** when LibreOffice is installed.

## [1.5.0] - 2026-10-24

Read scanned documents, work directly from shelves, and choose wheel actions faster.

### New

- **Recognise text in images and PDFs.** Drop screenshots, scanned documents,
  PDFs, or a mixture onto **OCR** in the Tools wheel. Save a text file or a
  searchable PDF beside each original. Recognition runs on your Mac in supported
  system languages, and PDF pages that already have text keep it.
- **Open the wheel for shelf selections.** Select files on a shelf and press
  **⌃⌥Space**, or choose **Open Wheel for Selection** from the menu bar.
  A focused shelf takes priority; otherwise the shortcut uses the most recent
  shelf when OpenShelf is active, or Finder's selection.
- **Numbered wheel shortcuts.** Hold **⌘** in a keyboard-opened wheel to see
  each wedge's number, then press the number to run it. **⌘0** opens More or
  Back; **⌘⌥number** opens conversion settings where available.

### Fixed and improved

- Shelves follow files moved through the Actions wheel, keeping their order,
  selection, and selection anchor.
- Command-line tools' final output is captured reliably, including an output
  line without a closing newline. ffmpeg progress stops when its job ends.
- Video preview frames remain usable after their temporary files are removed.
- PDF image exports render and save one page at a time to reduce memory use.
- Excel output exceeding 1,048,576 rows is refused before conversion. The
  preflight supports quoted records, CR/CRLF line endings, and UTF-16 files.
- Resize and Remove Background reuse rendering contexts, and Add Background's
  colour swatches fit their window.
- Child processes receive a limited environment rather than inherited shell
  credentials.
- Concurrent OCR jobs are queued to avoid stalls on smaller machines.
- OCR cancellation leaves no partial PDF output, rotated pages retain their
  displayed dimensions, and recognition retries on CPU if acceleration fails.

Thanks to **[David NeSmith (@djnesmith)](https://github.com/djnesmith)** for the
original features and fixes in his OpenShelf fork. The
[commit review and attribution](https://github.com/Manan-Santoki/OpenShelf/blob/main/docs/FORK_REVIEW.md)
records the source commits and adaptations.

## [1.4.0] - 2026-10-23

Choose how conversions come out.

### New

- **Output settings.** The new **Output** tab in Settings sets how converted
  files are made. Everything starts on **Automatic**, which converts exactly
  as before. Thanks to [@wada3n](https://github.com/wada3n) for the idea in
  [#4](https://github.com/Manan-Santoki/OpenShelf/issues/4).
  - **Video:** codec (H.264 or HEVC), quality (Smaller, Balanced, Best or a
    bitrate of your choice) and largest size (4K, 1080p, 720p or 480p). MP4
    and MOV don't need ffmpeg for any of these; MKV, WebM, AVI and WMV follow
    them through ffmpeg. HDR videos stay HEVC so their colours survive.
  - **Audio:** a fixed bitrate for MP3, M4A, OGG, Opus, WMA and the sound in
    videos.
  - **Images:** quality for JPEG, HEIC, AVIF and WebP, and whether to keep
    metadata such as the camera and location.
  - **GIFs from videos:** width and frame rate.
- **Choose just once with ⌥.** Hold **⌥** as you drop on a Convert wedge (or
  press **⌥Return** in the keyboard wheel) to pick the settings for that one
  conversion. Videos show roughly how big the result will be before you
  convert, and **Use as default** keeps your choice for next time.

## [1.3.0] - 2026-10-22

Every editor now works on several files at once.

### New

- **Edit several files at once.** Drop five photos on **Crop**, set the crop
  once, and it's applied to all of them. A filmstrip under the editor moves
  between files, a dot marks the ones with changes, and the button says how
  many will be saved ("Crop 5 Images"). Files you don't change are left alone.
  Thanks to [@nealjane](https://github.com/nealjane) for the idea in
  [#3](https://github.com/Manan-Santoki/OpenShelf/issues/3).
  - **Same for all** applies one edit to every file. Crops follow each file's
    size, so "the top half" stays the top half on images of any size. Turn it
    off to give each file its own edit. It's on for Crop, Edit, Add BG, video
    Crop, Rotate and PDF Split, and off to start with for Trim and Split.
  - **Annotate, Redact, Remove BG and PDF Pages** keep separate edits for each
    file, each with its own undo.
  - **Trim and Split** for video and audio can cut or split every file at the
    same times, and **Pages** and **Split** now take several PDFs.
- **Updates wait quietly.** When a new version is out, the menu-bar wheel
  turns blue and the menu offers **Update to Version…**, instead of a window
  popping up while you work.

### Improved

- The website moved to [openshelf.space](https://openshelf.space).

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
