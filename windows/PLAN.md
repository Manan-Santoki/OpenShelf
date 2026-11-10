# OpenShelf for Windows — plan

A native Windows app with the same UI and features as the Mac app, written from
`../docs/SPEC.md`. The Swift sources in `../Sources/OpenShelf` are the reference
for behaviour, constants and copy. C# on .NET 10, WPF, Windows 10 22H2+ and 11.

## Milestones

| | Scope | Status |
|---|---|---|
| M0 Spike | Mid-drag detection, wheels, basic shelf, tray, toast, drag harness | done |
| M1 Wheels and shelves | Paging, keyboard control, keyboard wheel and hotkey, Explorer selection, shake, all Actions; full shelves (stack, grid/list, selection, docking, hiding, restore, preview); settings window; progress card | done, QA pass run |
| M2 Convert | Images, archives, subtitles, spreadsheets, Markdown, PDF, documents, office, e-books, audio, video, GIF; output settings and the Alt options window; tool installer | done, see "Not yet verified" |
| M3 Tools | Tools wheel: image editors (Crop, Resize, Compress, Metadata, Edit, Annotate, Add BG, Remove BG, Redact, Read QR, OCR, Combine), PDF tools (Combine, Pages, Split, Compress, Metadata, OCR), media tools (Trim, Speed, Crop, Rotate, Split, Merge, Compress, Mute) | not started |
| M4 Rest | Tour, watched folders (UI and storage exist; watching does not), Explorer right-click entry, command line, installer, updater (WinSparkle), CI, README/SPEC annex | not started |

## Checkpoint (2026-11-09)

681 unit tests pass across nine test projects. Real synthetic drags pass for Convert, Copy Path, Shelf and hub-drop.
`ConversionWheelCatalog.ToolsProvider` in `OpenShelf.App` is the hook for the Tools wheel; `Commands.TakeTour` and
`Commands.CheckForUpdates` are placeholders that only toast.

### Not yet verified

- LibreOffice and calibre installs from Settings, Extra Formats (`msiexec /a` unpack was only tested with a fake runner).
- LibreOffice and calibre conversions with the real tools (neither is installed on the dev machine); transcription (no whisper).
- The Alt output-options window end to end from a wheel; Share; Move's overwrite protection in a real run.
- Shelf: Delete, type-to-select, Ctrl+W, list/grid toggle, the right-click menu items, auto-hide on the left edge.
- Settings: toggles surviving a restart; the keyboard wheel on a desktop-icon selection; a real OLE text drag.

### Known issues

- HEIC can be read but not written (the bundled ImageMagick has no HEIC encoder), so the HEIC wedge is hidden.
- Settings accepts "Convert = Alt" while "Tools = Shift+Alt" with no warning.
- The drag harness's own window occasionally misses the first mouse press after a pause; rerun once.

- Probe retry, grid-tile hit-testing, winget opt-in and the install phase text are fixed in code and unit/demo-tested; they still need a real-mouse check.

### Test tooling

- `OpenShelf.DragHarness.exe` drags a PNG from its own window onto a wheel (`--keys`, `--wedges`, `--wedge`, `--press`, `--shake`, `--expect`).
- `OpenShelf.DragHarness.exe qa "<script>"` scripts real mouse and keyboard input (move, click, drag, keys, shot, windows, guard).
- `OpenShelf.Shelf.Demo`, `OpenShelf.Settings.Demo` and `OpenShelf.Wheel.Demo` put UI into fixed states for screenshots without the mouse.

## Icons

One set everywhere: Lucide, embedded as geometries in `OpenShelf.Ui` (`Icons`, `IconView`; licence in `LICENSE-lucide.txt`; regenerate with
`scripts/gen-lucide.mjs`). `WheelEntry.Glyph` carries the Lucide name. Window, taskbar and tray icons come from `OpenShelf.ico` through
`AppIcons`. Never add a Segoe glyph.

## Projects

| Project | Holds | Rule |
|---|---|---|
| `OpenShelf.Core` | UI-free logic and the service contracts (`Contracts.cs`) | No WPF, no P/Invoke |
| `OpenShelf.Wheel` | Drag detection, the wheel window, wheel actions (`IWheelService`) | |
| `OpenShelf.Shelf` | Shelf windows, tray icon, toasts (`IShelfService`, `ITrayService`, `IToastService`) | |
| `OpenShelf.App` | Composition root only (`AppHost.cs`), manifest | Orchestrator-owned |
| `OpenShelf.Ui` | Theme, spring motion, frosted glass shared by every surface | |
| `OpenShelf.Settings` | Preferences store and the settings window (`IPreferences`, `ISettingsWindow`) | |
| `OpenShelf.Convert` | Conversion foundation: file kinds, format catalog, runner, external tools; image, archive, subtitle and spreadsheet engines. No UI | Headless, tested in `OpenShelf.Convert.Tests` |
| `OpenShelf.Convert.Markdown` / `.Documents` / `.Media` | Markdown, document/PDF/office/e-book, and ffmpeg engines, each registered by the app | Each has its own test project |
| `OpenShelf.Install` | Downloads ffmpeg, Pandoc, LibreOffice and calibre for Settings, Extra Formats | |
| `OpenShelf.Tests` | xUnit tests for Core | |
| `OpenShelf.DragHarness` | Console tool that performs a real file drag with synthetic input and checks the result | |

## Conventions

- **Coordinates:** every service boundary uses physical screen pixels (`ScreenPoint`, what `GetCursorPos` returns). Convert to WPF device-independent pixels only inside a window, using that monitor's DPI. The app is Per-Monitor-V2 DPI aware.
- **Never return Move** from a drop target: Explorer deletes the source after a Move. Answer Copy (or Link if Copy isn't allowed, else None).
- **Results** are saved next to the original and never overwrite: `OutputNaming.Destination`.
- **Overlay windows** (wheel, shelf, toast) never take focus: `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `ShowActivated=false`, topmost, not in the taskbar.
- **Look:** dark surfaces `#2A2A2E`/`#262629`, white hairlines at 15–30% opacity, accent `#3D7BFD`, Segoe UI. Wheel: outer radius 132, hub 50, wedge 0 at 12 o'clock going clockwise (`WheelGeometry`).
- **Logging:** append diagnostics to `%LOCALAPPDATA%\OpenShelf\logs\openshelf.log` through one small logger per project; no message boxes.
- **Builds:** each contributor builds only its own project with its own `--artifacts-path windows/.artifacts/<name>` so parallel builds don't collide.

## Mid-drag detection

Windows has no global drag pasteboard, so:

1. Poll the left button, pointer and modifier keys (~60 Hz). Button down and moved past the system drag threshold means a drag may be in progress.
2. Feed the modifiers to `ModifierChord`; `TriggerMatcher` maps the chosen chord to a wheel.
3. On a chord, show the wheel window centred on the pointer as a *probe*: its content is invisible, but its whole surface is 1/255 opaque so it hit-tests, and it is an OLE drop target.
4. If `DragEnter` arrives with a file list, show the wheel in that window. If it arrives without files, hide. If the button comes up first, hide.
5. `DragOver` highlights the wedge under the pointer; `Drop` on a wedge runs it with the paths taken at `DragEnter`.
6. The wheel stays until the button is released, wherever the pointer goes.

## Testing rules

- Only ONE thing may drive the real mouse at a time. Unless your brief says you own the mouse, never run the app with the drag harness.
- UI you cannot click is verified with a small demo executable in your own project folder that puts the window into each state by code, then a screenshot you read back.
- Engines are verified with headless xUnit tests on real files generated in a temp folder.
