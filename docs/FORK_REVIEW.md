# Review of djnesmith/OpenShelf

Reviewed on 2026-10-24 against local base `5858a05eb11e88f9bf0cc781be9ae6795c2fac7b` and fork `main` at `ab6afde1a1f17f4aa9d5d7fd11e320d23f84dae8`.
The comparison contains 53 non-merge commits and one upstream merge. This inventory was
built from Git history, every commit message and changed-file list, a structural
scan of every source patch, and detailed patch inspection for the fixes and integration
points; it is not a claim that every new feature has been tested or that every line in the fork received a security review.

## Authorship and license

All 53 fork-only non-merge commits are authored by **David NeSmith <git@ajdn.org>**.
The source and adapted regression tests below retain that attribution through their
linked original commits. Both repositories use the same MIT license and existing
“Copyright (c) 2026 OpenShelf contributors” notice, retained in `LICENSE`.
These local adaptations retain their linked original sources. The combined 1.5.0
integration credits David with a Co-authored-by trailer.
When committing an unchanged port, preserve the original Author (for example with
`git cherry-pick`). For adapted or combined commits, include the original source SHA
and `Co-authored-by: David NeSmith <git@ajdn.org>` in the commit message.

## Fixes applied

| Source commit | Change | Adaptation here |
| --- | --- | --- |
| [556ed2b](https://github.com/djnesmith/OpenShelf/commit/556ed2b2b9cb16ce66c6ea65fe4788b645bab570) | Drain subprocess stdout/stderr before checking exit status; flush the last unterminated output line. | Retain upstream ProcessFailure API; add output stress tests and close pipe writers on launch failure. |
| [207bc44](https://github.com/djnesmith/OpenShelf/commit/207bc4402d50e4ac3f1043576550f981600d9227) | Render and write PDF image pages individually. | Retain 1.4.0 image quality settings; OCR document ownership and per-page rendering are now also applied; PDF Images awaits that feature. |
| [309934c](https://github.com/djnesmith/OpenShelf/commit/309934c8badcf9195669ddfcab7073b56839abb6) | Reject XLSX output exceeding Excel’s row limit before building the table. | Apply CSV/TSV and writer checks; extend counting for CR/CRLF, BOM-marked UTF-16, quote rules and chunk boundaries; suggest CSV because Parquet is absent here. |
| [b7f0f32](https://github.com/djnesmith/OpenShelf/commit/b7f0f32311119fd16de0dbb65f228639c691ac9f) | Decode temporary ffmpeg PNGs before deleting them. | Test by saving the returned frame after the temporary file has gone. |
| [ab60495](https://github.com/djnesmith/OpenShelf/commit/ab604956ad1ff0d5665ab013ed1edc5191a01680) | Stop ffmpeg progress callbacks after the job finishes. | Retain original gate and regression test. |
| [81e412e](https://github.com/djnesmith/OpenShelf/commit/81e412e0071b81c752244118d7b27e62bb22ac34) | Share Core Image contexts for Resize and Remove Background. | Preserve unnamed ICC handling and compare rendered pixels. |
| [ab6afde](https://github.com/djnesmith/OpenShelf/commit/ab6afde1a1f17f4aa9d5d7fd11e320d23f84dae8) | Fit Add Background swatches in their window. | Test one-image and batch editor layouts. |
| [e24bfd8](https://github.com/djnesmith/OpenShelf/commit/e24bfd87fb0639f6b7bda96d7328a8e938b32d7c) | Give subprocesses an allowlisted environment. | Cover our current launch paths and Homebrew; also reject credential-like LC_* names; omit calibre-specific integration. |

The relocation portion of [a2c6d6f](https://github.com/djnesmith/OpenShelf/commit/a2c6d6f6bd07f4472d5551f7fccb8c7433eadc41)
(**David NeSmith <git@ajdn.org>**) is also applied; its shelf-selection wheel workflow has since been integrated: successful Moves update every shelf reference and schedule persistence.
The adaptation keeps the existing destination picker and collision behavior, returns
an explicit source-to-destination map, and additionally remaps selection anchors
and scroll targets. Regression tests cover successful and failed moves, preserved
selection, multiple shelf references, and Shift-selection after moving an anchor.

## Features applied

- **OCR** — David NeSmith’s [d0b71b4](https://github.com/djnesmith/OpenShelf/commit/d0b71b466e266074a6ec1d0fa72664b0a7357f28)
  plus the PDF lifetime and per-page rendering corrections in **207bc44**. Images,
  PDFs and mixed batches produce text or searchable PDF copies. Local adaptations
  propagate cancellation into the detached worker, use partial PDF writes, and
  explicitly map rotated PDF pages into the output page. A bounded retry on
  supported CPU devices handles accelerator failures observed during concurrent
  media tests; a forced-failure test exercises that fallback.
- **Shelf-selection wheel** — David NeSmith’s **a2c6d6f**. The global shortcut and
  menu item prefer the focused shelf, then the recent shelf when OpenShelf is
  active, and otherwise Finder. Store-URL injection (also present in David’s
  **45756ef**) isolates integration tests from the user’s saved shelves.
- **Numbered wheel shortcuts** — David NeSmith’s [e543433](https://github.com/djnesmith/OpenShelf/commit/e543433af1759c31da26233778e32368ea285a08).
  Command reveals numbered wedges, Command-number runs one, Command-0 pages.
  Adapted to preserve 1.4.0’s Option-to-configure behavior using event modifiers;
  unrelated Command shortcuts and plain number keys pass through.

## Commit-by-commit disposition

“Candidate” means potentially useful, not approved for inclusion or verified locally.
Remaining feature candidates are separate from the applied fixes and features above.

| Commit | Original author | Change | Disposition |
| --- | --- | --- | --- |
| [28bb48c](https://github.com/djnesmith/OpenShelf/commit/28bb48ca20460f3712d1a88b0251d7ac624e993c) | David NeSmith | Remove Sparkle in-app updates | Do not port: removes upstream’s signed update mechanism to suit a fork. |
| [f036332](https://github.com/djnesmith/OpenShelf/commit/f0363323fc99d17b0b4934fd4b8bde68f81c72eb) | David NeSmith | Sign with David's Apple Development identity | Do not port: personal signing identity and bundle identifier. |
| [b97e541](https://github.com/djnesmith/OpenShelf/commit/b97e5415eec9aa1ab7b9556262c9e35efb5b1eaa) | David NeSmith | Save a still frame from a video | Recommended next: still frame; temporary-image decoding fix is already included here. |
| [e92b999](https://github.com/djnesmith/OpenShelf/commit/e92b9992f2c16f587cac2449958519d0d8b81502) | David NeSmith | Reverse video and audio | Candidate: additive feature; requires its own integration and regression testing. |
| [8e3ee6c](https://github.com/djnesmith/OpenShelf/commit/8e3ee6c7f78b08687be9e3fd8b0169bb35f5fa60) | David NeSmith | Strip metadata from video and audio | Candidate: additive feature; requires its own integration and regression testing. |
| [56220fa](https://github.com/djnesmith/OpenShelf/commit/56220fa04cf4ffed229487e74e4e7174ebe5e4cd) | David NeSmith | Mix audio down to mono | Candidate: additive feature; requires its own integration and regression testing. |
| [927e854](https://github.com/djnesmith/OpenShelf/commit/927e8542fa9d4b477d0391bcd9a0f6e77c9fb056) | David NeSmith | Even out loudness to -16 LUFS | Candidate: additive feature; requires its own integration and regression testing. |
| [911be86](https://github.com/djnesmith/OpenShelf/commit/911be862f5ab638800d49d4d2329f9402098821d) | David NeSmith | Flip video vertically and make it grayscale | Candidate: additive feature; requires its own integration and regression testing. |
| [4cd1c39](https://github.com/djnesmith/OpenShelf/commit/4cd1c39ba17d1e3728574be8af1c4aacb1eacf75) | David NeSmith | Convert ebooks with calibre | Candidate: additive feature; requires its own integration and regression testing. |
| [e5e1b51](https://github.com/djnesmith/OpenShelf/commit/e5e1b51216cfbf1c7ed5c01ccd943f6619c1dd3f) | David NeSmith | Convert markup documents with pandoc | Candidate: pandoc conversion; retain sandbox and document linked-resource limits. |
| [946b13a](https://github.com/djnesmith/OpenShelf/commit/946b13a548be61854c373424f0ee66d3c644bdd5) | David NeSmith | Convert data files: JSON, YAML, XML, property lists, CSV | Candidate: additive feature; requires its own integration and regression testing. |
| [18c4a86](https://github.com/djnesmith/OpenShelf/commit/18c4a869b535cf4572755f31756b8f110cf3e0e6) | David NeSmith | Make video and audio louder or quieter | Candidate: additive feature; requires its own integration and regression testing. |
| [d0b71b4](https://github.com/djnesmith/OpenShelf/commit/d0b71b466e266074a6ec1d0fa72664b0a7357f28) | David NeSmith | Recognise text in images and scanned PDFs | Applied with PDF ownership, per-page rendering, cancellation and rotation tests. |
| [9314e81](https://github.com/djnesmith/OpenShelf/commit/9314e81593284a43bfcf9c8b6c87081e3a86ed3b) | David NeSmith | Transcribe audio and video on this Mac | Candidate: on-device transcription; depends on recognizer availability, permissions and language support. |
| [83e530c](https://github.com/djnesmith/OpenShelf/commit/83e530c962a579f5ff03e26363d738c2b918318d) | David NeSmith | Fit video and audio under a size | Recommended next: fit media under a target size; needs ffmpeg and end-to-end output-size checks. |
| [ebe700d](https://github.com/djnesmith/OpenShelf/commit/ebe700d9429565cf3c9bc7f5882ad276a7679bb6) | David NeSmith | Compress audio at a chosen bitrate | Candidate: additive feature; requires its own integration and regression testing. |
| [f6ab6eb](https://github.com/djnesmith/OpenShelf/commit/f6ab6eb738e3faa3d166188f77b46253870a4e9f) | David NeSmith | Save chosen PDF pages as images at a chosen resolution | Candidate: additive feature; requires its own integration and regression testing. |
| [b9382f1](https://github.com/djnesmith/OpenShelf/commit/b9382f1a174d7231fbfdb65144e9520d49fe6288) | David NeSmith | Crop to the subject Vision finds | Recommended next: crop to subject; integrate with current batch crop state. |
| [cc96f17](https://github.com/djnesmith/OpenShelf/commit/cc96f1751e2d8211e56f8ad78ee09942b9eb9482) | David NeSmith | Change a video's frame rate | Candidate: additive feature; requires its own integration and regression testing. |
| [e684f12](https://github.com/djnesmith/OpenShelf/commit/e684f12857ccd37dbcf75da8208a486fd9f88c0e) | David NeSmith | Set how many times a GIF plays | Candidate: additive feature; requires its own integration and regression testing. |
| [0b6f8df](https://github.com/djnesmith/OpenShelf/commit/0b6f8dff3422b8e84a2865606f8d63441f8f6695) | David NeSmith | Make icons, and animated PNGs from GIFs and video | Candidate: additive feature; requires its own integration and regression testing. |
| [d5dc473](https://github.com/djnesmith/OpenShelf/commit/d5dc473ac18d6232abbb49da329c094ffbf2663f) | David NeSmith | Export video in a chosen codec: H.264, HEVC, AV1, VP9, ProRes | Candidate: additive feature; requires its own integration and regression testing. |
| [37100fe](https://github.com/djnesmith/OpenShelf/commit/37100fee2c215a9613f58e8c4d1fc5c2e1b38152) | David NeSmith | Export audio as AAC, ALAC, CAF, PCM or Vorbis | Candidate: additive feature; requires its own integration and regression testing. |
| [fe9573e](https://github.com/djnesmith/OpenShelf/commit/fe9573e563e91f3e3ebef2c972aab91ffc72e136) | David NeSmith | Pack folders and files as tar.xz, tar.zst and 7z | Candidate: additive feature; requires its own integration and regression testing. |
| [7020449](https://github.com/djnesmith/OpenShelf/commit/70204498871eb16cb8b800507fbc44b05c235e4f) | David NeSmith | Convert subtitles to and from ASS, SSA and TTML | Candidate: additive feature; requires its own integration and regression testing. |
| [ec3644a](https://github.com/djnesmith/OpenShelf/commit/ec3644ae8cea61dad44deaae0bb640583d802704) | David NeSmith | Export video as MPEG, TS, M2TS or VOB, and sound as MKA | Candidate: additive feature; requires its own integration and regression testing. |
| [c953578](https://github.com/djnesmith/OpenShelf/commit/c9535785ac0cb244b97f2887777a04d30aed606d) | David NeSmith | Write JPEG XL with cjxl | Candidate: additive feature; requires its own integration and regression testing. |
| [e31f6ad](https://github.com/djnesmith/OpenShelf/commit/e31f6adc25cf89da7bd51d8c87a76e781082c603) | David NeSmith | Convert Parquet and SQLite | Candidate: Parquet/SQLite conversion; keep safe mode, extension restrictions and Excel limit checks. |
| [2242aa9](https://github.com/djnesmith/OpenShelf/commit/2242aa9c3e944ad9e4a079c0330a10613ca4136b) | David NeSmith | Run calibre without the GPU | Defer: calibre crash fix; bring with ebook conversion. |
| [e24bfd8](https://github.com/djnesmith/OpenShelf/commit/e24bfd87fb0639f6b7bda96d7328a8e938b32d7c) | David NeSmith | Give every child process a minimal environment | Applied/adapted in this working tree; original author credited below. |
| [755a174](https://github.com/djnesmith/OpenShelf/commit/755a1743bea87c76ca189c38c805825258df02a9) | David NeSmith | Open new shelves expanded | Preference change: expanded shelves by default, rather than a bug fix. |
| [45756ef](https://github.com/djnesmith/OpenShelf/commit/45756efd6e31a547596f4cc9e419b2affb2d7751) | David NeSmith | Hide Shelves, and Show Shelves to bring them back | Candidate: toggle visibility of all shelves; test edge docking and persistence. |
| [74466c7](https://github.com/djnesmith/OpenShelf/commit/74466c79b3f89382d2da1ac8d517622f8297b58c) | David NeSmith | Keep an audio file's format when editing it | Recommended next: preserve audio format; spans media routing, encoders and save notices. |
| [f67fca7](https://github.com/djnesmith/OpenShelf/commit/f67fca74a5775248dea408bb51e46a0974cfc5d2) | David NeSmith | Keep a video's container and codecs when editing it | Recommended next: preserve video container/codecs; depends on audio preservation. |
| [b7f0f32](https://github.com/djnesmith/OpenShelf/commit/b7f0f32311119fd16de0dbb65f228639c691ac9f) | David NeSmith | Read ffmpeg's frames before deleting them | Applied/adapted in this working tree; original author credited below. |
| [ab60495](https://github.com/djnesmith/OpenShelf/commit/ab604956ad1ff0d5665ab013ed1edc5191a01680) | David NeSmith | Drop ffmpeg progress that arrives after the job ends | Applied/adapted in this working tree; original author credited below. |
| [258b3e3](https://github.com/djnesmith/OpenShelf/commit/258b3e34e5d418426eaa1bd35bf459e6d39ac400) | David NeSmith | Edit MKV, WebM and AVI with ffmpeg, keeping their format | Recommended next: native editing of MKV/WebM/AVI through ffmpeg; depends on KeepFormat. |
| [e543433](https://github.com/djnesmith/OpenShelf/commit/e543433af1759c31da26233778e32368ea285a08) | David NeSmith | Run keyboard-wheel wedges with ⌘ and a number | Applied with paging, pass-through and Option-to-configure integration tests. |
| [aaf2e3f](https://github.com/djnesmith/OpenShelf/commit/aaf2e3f3278a774eb31df760613a9d6454a21494) | David NeSmith | Convert EDN to and from JSON and YAML with jet | Candidate: additive feature; requires its own integration and regression testing. |
| [4fb8b48](https://github.com/djnesmith/OpenShelf/commit/4fb8b4864acae4355ae6e2908fea8b7daf9ddc34) | David NeSmith | Open folders in iTerm2 when it's installed | Candidate: iTerm preference; consider whether automatic app selection fits upstream. |
| [9237917](https://github.com/djnesmith/OpenShelf/commit/923791765de9aa4160c7df8f95ef688336a453d9) | David NeSmith | Copy Name, next to Copy Path | Candidate: Copy Name; small independent addition, but adds Actions-wheel paging. |
| [9dcc640](https://github.com/djnesmith/OpenShelf/commit/9dcc64087f8aa6d67a39e08ca1afa2adf67336b7) | David NeSmith | Copy to a chosen folder, like Move | Consider separately: changes Copy from clipboard to a destination-folder picker. |
| [634e18d](https://github.com/djnesmith/OpenShelf/commit/634e18d288bf47b761cee285bb718c74dac0764a) | David NeSmith | Ask about name clashes when moving or copying | Consider separately: conflict dialog changes Move/Copy behavior and permits replacement. |
| [a2c6d6f](https://github.com/djnesmith/OpenShelf/commit/a2c6d6f6bd07f4472d5551f7fccb8c7433eadc41) | David NeSmith | Open the wheel shortcut on selected shelf items | Applied: shelf-selection wheel and Move reference repair; preserve upstream selection anchors and persistence. |
| [207bc44](https://github.com/djnesmith/OpenShelf/commit/207bc4402d50e4ac3f1043576550f981600d9227) | David NeSmith | Render PDF pages one at a time | Applied/adapted in this working tree; original author credited below. |
| [309934c](https://github.com/djnesmith/OpenShelf/commit/309934c8badcf9195669ddfcab7073b56839abb6) | David NeSmith | Refuse Excel output past Excel's row limit | Applied/adapted in this working tree; original author credited below. |
| [556ed2b](https://github.com/djnesmith/OpenShelf/commit/556ed2b2b9cb16ce66c6ea65fe4788b645bab570) | David NeSmith | Never lose a tool's last output | Applied/adapted in this working tree; original author credited below. |
| [81e412e](https://github.com/djnesmith/OpenShelf/commit/81e412e0071b81c752244118d7b27e62bb22ac34) | David NeSmith | Share Core Image contexts between editor renders | Applied/adapted in this working tree; original author credited below. |
| [d015597](https://github.com/djnesmith/OpenShelf/commit/d015597bb525f0b9500313d7ae3bf883145478d1) | David NeSmith | Add FORK-FEATURES.md: what the fork adds over upstream | Reference only: fork inventory; Git history is the source for this review. |
| [c0d67ee](https://github.com/djnesmith/OpenShelf/commit/c0d67ee16b7db22148e6bbd3b1d1dde278a8488b) | David NeSmith | Pretty Print source files | Candidate: source formatting; retain stdin input, isolated working folder and explicit configuration. |
| [85d04a5](https://github.com/djnesmith/OpenShelf/commit/85d04a59aa20c0efa4ac3b2d602c52aa7a72231c) | David NeSmith | List every tool in Settings and name it when it's missing | Bring alongside new external tools: discovery UI and missing-tool messages. |
| [3044e8d](https://github.com/djnesmith/OpenShelf/commit/3044e8dfe011a956e1e039e037cdff7640de2d44) | David NeSmith | Fit the Export sheet's formats and codecs | Defer: fixes clipping in the fork-only Export editor; bring with that feature. |
| [2909710](https://github.com/djnesmith/OpenShelf/commit/2909710bfe91e6ae00e63b67d01c704feefb81b9) | David NeSmith | Merge upstream 1.4.0: output settings, and editing several files at once | Reference only: upstream merge; retain our batch editors and output settings. |
| [ab6afde](https://github.com/djnesmith/OpenShelf/commit/ab6afde1a1f17f4aa9d5d7fd11e320d23f84dae8) | David NeSmith | Fit the Add Background swatches in their window | Applied/adapted in this working tree; original author credited below. |

## Local validation of the fixes batch

- `make app` succeeded; `codesign --verify --deep --strict` validated the resulting
  `build/OpenShelf.app`. `git diff --check` also passed.
- Full Swift Testing suite: **485 tests reported in 103 suites, successful**, with
  16 conditional tests skipped (network/install tests, optional render tests and
  disabled native GIF cases). ffmpeg end-to-end cases and a real LibreOffice
  conversion ran successfully. See the test source for each condition.
- Included a 2,000-process concurrent error-output stress test, a 200-run final
  unterminated-line test, 40-page PDF memory check (rise below 700 MiB), image
  pixel comparisons, temporary-frame re-saving and one/batch editor layout tests.
- Baseline reproduction using an isolated archive of the original local HEAD:
  the final-output test failed on all 200 runs; Add Background measured 471 pt
  against a 460 pt window for both one and three images. Both pass with the fixes.
- Local SwiftPM could not load the unchanged manifest: the installed Command Line
  Tools' PackageDescription API has an interface/link mismatch. The full suite was
  instead compiled directly with Swift 6, the macOS 26 SDK, a testable OpenShelf
  library, the installed Testing framework and its macro plugin, then executed
  through Testing.__swiftPMEntryPoint. This tests all repository test sources;
  it does not claim that `swift test` succeeded on this toolchain.
- The direct test binary is `.build/local-tests/OpenShelfTests`; on this Mac it
  needs `DYLD_LIBRARY_PATH=/Library/Developer/CommandLineTools/Library/Developer/usr/lib`.
  The optional positional arguments to this locally generated runner filter test
  names as regular expressions. The normal project/CI command remains `swift test`.
- Pre-existing actor-isolation warnings in OutputSettingsTests remain outside this patch.

## Local validation of the feature batch

- Full suite: **501 tests reported in 107 suites, successful**, with the same
  16 conditional skips. Used the direct Swift Testing runner described above.
- Real Vision OCR produced searchable PDFs and text. Checks cover original PDF
  text retention, document lifetime, rotated pages, locked PDFs, preferred
  languages, cancellation cleanup, and a forced accelerator-failure CPU retry.
- Keyboard integrations cover numbered paging, unrelated shortcuts, plain digits,
  selected shelf URLs, and Option-Command-number output settings. Shelf-manager
  focus and recent-selection integration uses real temporary files and an isolated
  persistence URL; no user shelves are modified.
- `make app`, strict deep signature verification, and `git diff --check` succeeded.
- Rendered and visually checked the OCR editor and numbered wheel. Local previews:
  `build/feature-previews/ocr.png` and `build/feature-previews/wheel-shortcuts.png`.
- Original source attribution is retained above and in the imported OCR and test
  files. Release preparation and contributor credit are recorded in CHANGELOG.md.
