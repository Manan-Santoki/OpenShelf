# Changelog

All notable changes to OpenShelf. The newest version at the top is released
automatically: push it to `main` and the Release workflow builds the app,
tags `v<version>` and publishes a GitHub release with that section as notes.

Format: one `## [x.y.z] - YYYY-MM-DD` heading per version, newest first.

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
