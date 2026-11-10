# OpenShelf.Convert

Headless conversion foundation (no WPF). Port of `Sources/OpenShelf/Convert` with Swift names kept where possible.
Tests: `OpenShelf.Convert.Tests`. Build and test with `--artifacts-path .artifacts/convert`.

## Entry points

- Formats for some paths: `ConversionCatalog.Formats(paths, ExternalToolsProvider.Default.Current)` (max 12, last is ZIP).
  Empty-wheel text ("MKV needs ffmpeg"): `MissingToolAdvice.EmptyMessage(paths, formats, tools)`.
- Run a batch: `ConversionRunner.Default.ConvertAsync(paths, format, options, IProgress<BatchProgress>, ct, onJobSaved)`
  returns a `BatchResult` (`Saved`, `Failures`, `Cancelled`, per-file `Jobs`). `BatchProgress(Completed, Total, Fraction, CurrentFile)`
  maps straight onto `IProgressCard.Report(completed, fraction)`. `ConversionRunner.Title(format)` is the card title.
- One file, no runner: `Converter.Default.ConvertAsync(new ConversionRequest(path, format, options), progress, ct)`.

## Adding an engine

1. Implement `IConversionEngine`: `CanConvert(FileKind, OutputFormat)` (pure, no I/O). If the answer depends on the extension or the
   installed tools override `CanConvert(ConversionRequest)` as well (see `SpreadsheetEngine`, which leaves XLS/ODS to LibreOffice).
2. Register it in `EngineRegistry.BuiltIn()` (or `EngineRegistry.Default.Register(engine, priority)`). Higher priority is asked first, equal
   priority in registration order: register LibreOffice/Pandoc/calibre engines ahead of built-in fallbacks and let their `CanConvert`
   check `request.ToolsOrDefault`. Fallback-on-failure ("if Pandoc fails the built-in runs") is the engine's own job.
3. `ConvertAsync` gets a `ConversionRequest` (`SourcePath`, `Format`, `Options`, `AllSources`, `Tools`) and returns the paths it wrote.
   Report 0..1 through `progress`, honour the token, throw `ConversionException.Failed("...")` for messages meant for people;
   anything else is run through `FailureMessage.Describe`.

## The temp-file rule

Engines never choose a final name. Write with `request.OutputPath("Name.ext")` (or `OutputFolderPath("Folder")`). Under the runner
that is a file inside a hidden `.os-xxxxxxxx` folder next to the source (same volume); only after `ConvertAsync` returns does the runner
move each result into the source folder with `OutputNaming.UniquePath` (`Name.ext`, `Name 2.ext`, ...) and delete the staging folder, also on
failure or cancel. So half-written files never appear and nothing is overwritten. Called directly (no runner) `OutputPath` picks a unique
name beside the source. Several files per name pattern ("Name Slide 2.jpg") are just several `OutputPath` calls. Never create siblings
yourself; clean up your own partial output when you fail.

## Running tools and faking them

- `ProcessRunner.RunAsync(exe, args, ProcessOptions, ct)`: no window, minimal environment (`ChildEnvironment`), bounded capture, timeout,
  cancel kills the whole tree (job object). Throws `ProcessFailure` (non-zero exit, crash, timeout) or `MissingToolException`.
- Engines that call tools take a `ToolRunner` (`ToolInvocation` in, lines out) defaulting to `ToolRunners.Default`; tests pass a fake that records
  the invocation and writes what the tool would have.
- Tool availability: `ExternalTools` (record of paths; `with { Ffmpeg = "x" }`). `ExternalToolsProvider` caches (5 s, or until `Refresh()`);
  `ExternalToolsProvider.Default` can be replaced at startup/in tests with `new ExternalToolsProvider(() => fakeTools)`, or give a `Converter`
  its own provider: `new Converter(registry, provider)`. `ToolLocations.ForEnvironment(getEnv, fileExists)` fakes the disk for detection tests.
  Search order: `%LOCALAPPDATA%\OpenShelf\tools\<tool>\` (and `\bin`), PATH, then usual install places. `OPENSHELF_IGNORED_TOOLS=ffmpeg,pandoc` hides
  PATH copies. Tests needing a real optional tool should return early (or use `Skip`) when `ExternalTools.Detect(ToolLocations.Standard)` lacks it.

## OutputFormats and engines (wave 1)

Engine: Images = `ImageEngine` (Magick.NET), Archives = `ArchiveEngine`, Subtitles = `SubtitleEngine`, Sheets = `SpreadsheetEngine`. "later" = no engine yet.

| Format | Engine | Format | Engine |
|---|---|---|---|
| PNG JPG WEBP TIFF AVIF BMP GIF | Images | MP4 MOV M4A MP3 WAV AIFF FLAC OGG OPUS WMA MKV WEBM AVI WMV | later (ffmpeg) |
| HEIC | catalog hides it: no HEIC encoder in the bundled ImageMagick (read only) | SRT VTT | Subtitles (also from audio/video later: whisper) |
| PDF | from images only; PDF/doc/sheet/slide sources later | TXT | Subtitles; documents/PDF later |
| DOCX | from images only | RTF HTML MD EPUB PPTX ODT | later |
| XLSX CSV TSV | Sheets (PDF source later) | ODS | later (LibreOffice) |
| ZIP TAR TGZ EXTRACT | Archives | MOBI AZW3 | later (calibre) |
| PAGES NUMBERS KEY | never on Windows (no Apple apps) | | |

## Deliberate differences from the Mac

- Audio and video need ffmpeg for everything (no AVFoundation); without it they are ZIP-only. `ConversionCatalog.Formats` takes an optional
  `hasAudioTrack` callback for the ffprobe silent-video check.
- WebP is written in-process, so it is on the wheel without cwebp; `CanEncodeWebP/Heic/Avif` come from what the bundled library can write.
- Subtitle and CSV readers accept BOM, UTF-16 and Windows-1252 text. Tar entries use the same one-name-per-top-level rule as ZIP.
