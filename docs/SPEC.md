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
- Blooms out from the pointer with a spring, petals staggered; shrinks away
  after a drop. Respects Reduce Motion.
- Actions confirm themselves with a small toast next to the pointer ("Copied
  path", "Moved 2 files to Desktop"), announced to VoiceOver. Actions that open
  their own UI (shelf, AirDrop, Terminal) don't toast.
- Stays on top of everything, on every Space, including full-screen apps.

### 2.1 Wheels

| Wheel | Default trigger | Status |
|---|---|---|
| Actions | ⌃ Control | v0.1 |
| Convert | ⇧ Shift | planned (§6) |
| Tools | ⇧⌥ Shift-Option | planned (§6) |

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
  selected item drags the whole selection.
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
- **Close:** header × button; the shelf also closes when its last item is
  removed. Closing never touches the files.
- Multiple shelves may exist at once.

## 4. Menu-bar item

New Shelf · Close All Shelves · Settings… · Quit.

## 5. Settings

- Trigger keys per wheel (any combination of ⌃ ⌥ ⇧ ⌘, at least one).
- Remove files from a shelf after dragging them out (default off).
- Open at login.

## 6. Roadmap

1. **Convert wheel** — wedges chosen by input type (e.g. PDF → DOCX/JPG/PNG/TXT;
   image → PNG/WEBP/HEIC/TIFF/AVIF/BMP/PDF/DOCX). Output is saved beside the
   original as a new file and never overwrites (`Name.jpg`, `Name 2.jpg`, …).
   Images via ImageIO/Core Image, audio/video via AVFoundation (ffmpeg optional
   for formats Apple frameworks lack), PDF via PDFKit, text/RTF/DOCX via
   NSAttributedString.
2. **Tools wheel** — hub shows the file size until a wedge is hovered. Tools:
   compress (quality or target size), crop, metadata view/strip, redact,
   annotate, add background, edit (adjustments), trim/split video & audio,
   PDF merge/split/reorder, QR decode. Tools that need input open a small
   editor window, and save a suffixed copy (`Name Cropped.jpg`), so results can
   be chained:
   - *Crop:* aspect presets (Free, 1:1, 16:9, 9:16, 4:3, 3:4), exact W×H in px,
     drag handles, Reset / Apply.
   - *Add background:* gradient and solid swatches or a custom image; padding,
     corner radius and shadow sliders; output ratio (Auto, 1:1, 16:9, 4:5).
3. Keyboard control of an open wheel (arrows + Return).
4. Shelf auto-hide: docked shelves slide into a slim tab and reveal on hover.
5. Rubber-band selection and type-to-select in expanded shelves.
6. Shelf persistence across relaunches (security-scoped bookmarks).
7. Batch progress window for long-running conversions.
