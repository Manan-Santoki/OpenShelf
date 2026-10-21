# OpenShelf

An open-source macOS menu-bar utility for working with files mid-drag.

Start dragging any file and tap a key — a wheel appears under the pointer. Drop
the file on a wedge and it's done, saved right next to the original.

- **⇧ Convert wheel:** images, PDFs, documents, audio, video, subtitles and
  archives into the formats that make sense for them (JPG, PNG, HEIC, AVIF,
  WEBP, PDF, DOCX, TXT, MP3, WAV, GIF, ZIP, …)
- **⇧⌥ Tools wheel:** Crop, Add Background, Annotate, Redact, Edit, Compress,
  Metadata — each in a small focused editor
- **⌃ Actions wheel:** Shelf · Add to Shelf · AirDrop · Copy · Move · Copy Path · Terminal
- **Shelves:** stacked preview, expandable grid/list, multi-select, drag out to
  any app, Quick Look (Space), dock to the left/right screen edge, multiple shelves
- Works system-wide with **no Accessibility permission**
- No network access, no telemetry, no dependencies

See [docs/SPEC.md](docs/SPEC.md) for the full behaviour and roadmap. Optional:
`brew install ffmpeg webp` adds MP3, GIF and WebP output.

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
```

You can also open `Package.swift` in Xcode.

## Project layout

```
Sources/OpenShelf/
  App/            entry point, app delegate, preferences
  DragDetection/  system-wide drag + modifier polling
  Wheel/          wheel items, geometry, overlay panel, SwiftUI rendering
  Convert/        format catalog, converters, progress runner
  Tools/          image tools, editor window host, editors
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
assets and code you don't have the rights to — all artwork is SF Symbols and
SwiftUI drawing.
