# OpenShelf for Windows — plan

A native Windows app with the same UI and features as the Mac app, written from
`../docs/SPEC.md`. The Swift sources in `../Sources/OpenShelf` are the reference
for behaviour, constants and copy. C# on .NET 10, WPF, Windows 10 22H2+ and 11.

## Milestones

| | Scope | Status |
|---|---|---|
| M0 Spike | Mid-drag detection, Convert and Actions wheels (few real wedges), basic shelf, tray icon, toast, drag test harness | done |
| M1 Wheels and shelves | All three wheels with paging and keyboard, every Action, full shelves (stack, grid/list, selection, docking, hiding, restore), settings window, global hotkey, Explorer selection | |
| M2 Convert | Image, PDF, document, Markdown, spreadsheet, media (ffmpeg), archive engines; progress card; output settings; tool installer | |
| M3 Tools | Image, PDF and media editors; OCR; Remove BG; QR | |
| M4 Rest | Tour, watched folders, shake trigger, Explorer context menu, CLI, installer, updater | |

## Projects

| Project | Holds | Rule |
|---|---|---|
| `OpenShelf.Core` | UI-free logic and the service contracts (`Contracts.cs`) | No WPF, no P/Invoke |
| `OpenShelf.Wheel` | Drag detection, the wheel window, wheel actions (`IWheelService`) | |
| `OpenShelf.Shelf` | Shelf windows, tray icon, toasts (`IShelfService`, `ITrayService`, `IToastService`) | |
| `OpenShelf.App` | Composition root only (`AppHost.cs`), manifest | Orchestrator-owned |
| `OpenShelf.Ui` | Theme, spring motion, frosted glass shared by every surface | |
| `OpenShelf.Settings` | Preferences store and the settings window (`IPreferences`, `ISettingsWindow`) | |
| `OpenShelf.Convert` | Conversion engines, file kinds, the format catalog, external tools. No UI | Headless, tested in `OpenShelf.Convert.Tests` |
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
