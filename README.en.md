# RenamePro

[简体中文](README.md) | English

A Windows tray utility that converts file contents for real when you change a file extension in File Explorer (for example, renaming `a.jpg` to `a.png`).

It adds no shell context-menu entries, opens no custom main window and never shows a console window: after double-clicking the executable the only visible artifact is a tray icon, and from then on the program simply follows the renames you perform in File Explorer.

## 1. Overview

Changing a file extension on Windows does not change the file contents, so you often end up with a broken file whose name says PNG while its bytes are still JPEG (image viewers fail, media players report errors). RenamePro watches file rename events across the system, detects the case where "the extension changed and both the old and the new extensions belong to the same media category", and then does three things automatically:

1. Copies the renamed file byte for byte into an original-format copy (`a - 副本.jpg` on Chinese systems, `a - copy.jpg` on English systems) so that nothing is lost;
2. Converts the file contents into the new format, writes the result to a temporary file in the same folder and atomically replaces the renamed file;
3. Reports progress through the same system progress dialog that File Explorer uses, and the operation can be cancelled.

### What's new in v1.2.0

- **Document conversion**: renaming `report.docx` to `report.pdf`, `slides.pptx` to `slides.pdf` or `notes.md` to `notes.docx` now performs the real conversion (Office / ODF / Markdown / HTML / CSV ↔ PDF, plus same-family conversions) through a headless LibreOffice or Office COM, with magic-number verification, cross-family refusal, timeouts and stepwise degradation;
- **Three release packages**: the image and audio/video builds stay single-file exes; the new **all-in-one build** is `RenamePro.exe` plus a `LibreOffice\` folder, so document conversion works out of the box with no first-run extraction;
- **The document engine is no longer embedded**: the all-in-one executable drops from 338.8 MB to 83.2 MB and the disk footprint from about 1.06 GB to about 786 MB (see "Why the document engine is a directory");
- **New troubleshooting entries**: `RenamePro.exe --selftest` (built-in self-test, exit code is the verdict, also written to `selftest-report.txt`) and `RenamePro.exe --docdiagnose <file> [target-ext]` (runs one real conversion and prints the engine source, magic sniffing and matrix verdict);
- **New engine build and verification scripts**: `setup-lo-dev.ps1` (unpack the official MSI), `build-docengine.ps1` (trim it into the bundled folder), `verify-docengine.ps1` (`-Soffice` / `-EngineDir` / `-Package` end-to-end modes); the test guide is [docs/测试方法.md](docs/测试方法.md:1) (Chinese).

## 2. Key Features

### Watching and Filtering

- Watches **all fixed drives** (can be limited with `watchDrives`) and recurses the whole drive tree through `IncludeSubdirectories`;
- Performance first: `NotifyFilter` is set to `FileName` only (rename events depend on file-name notifications alone) and the internal buffer is 256 KB;
- If a watcher fails it is rebuilt automatically with exponential backoff (`1s / 5s / 30s`), and the time window in which events may have been lost to a buffer overflow is written to the log;
- Four-stage early-exit filter, cheapest check first: internal-operation suppression table, extension whitelist, excluded directories, excluded file-name patterns;
- Excluded locations: `C:\Windows`, `C:\Program Files`, `C:\Program Files (x86)`, `$Recycle.Bin`, `System Volume Information`; excluded names: files starting with `~$` (Office temporary files) or `.`;
- 300 ms debouncing: rapid consecutive renames are merged so that only the last one is processed, which also avoids handle contention right after an Explorer rename;
- Re-entry guard: while a path is being processed, repeated triggers for the same path are skipped;
- Before converting, the file header (magic number) is read to verify that the real format matches the source extension; a mismatch is skipped and the reason is logged.

### Image Conversion (Magick.NET)

- `png / jpg / jpeg / bmp / gif / webp / tiff` convert to each other;
- Only JPEG output is lossy (quality configurable through `imageQuality`, default 92); every other output is lossless (WebP uses lossless encoding);
- GIF sources follow `gifPolicy`: `first-frame` converts the first frame only, `skip` ignores them;
- Combinations involving `.ico` are not converted (silently skipped) so that unstable icon files are never produced.

### Audio and Video Conversion (FFmpeg subprocess)

- **Stream copy first**: `ffprobe` inspects the stream codecs; if every codec is in `h264 / hevc / aac / ac3 / opus / vorbis / mp3` and the target container accepts them, only the container is rewritten with `-c copy` - the cost is nearly independent of file size and usually finishes in seconds;
- When streams are incompatible or a codec is not whitelisted, the file is re-encoded (video: `libx264 -crf 23 -preset veryfast` plus `aac`; WebM targets switch to `libvpx-vp9 + libopus` and WMV targets to `wmv2 + wmav2`);
- Audio encoders are chosen by the target format: `libmp3lame -q:a 2` for mp3, `aac -b:a 128k` for aac/m4a, `libvorbis` for ogg, `flac` for flac and `pcm_s16le` for wav; lossy-to-lossless conversion is not blocked but the log notes that it will not improve quality;
- Cross-category conversions: video to audio extracts the audio track, audio to a video container muxes the audio stream only (stream copy when possible);
- Progress is computed by parsing `out_time` from `-progress pipe:1` against the total duration, and the complete `stderr` output is captured for error details;
- When `ffmpeg.exe` is missing, **only audio/video support is disabled**; image conversion keeps working and the log states this explicitly.

### Document Conversion (bundled LibreOffice / system LibreOffice / Office COM)

- Reuses the same rename-driven flow: renaming `report.docx` to `report.pdf`, `slides.pptx` to `slides.pdf` or `notes.md` to `notes.docx` now performs the real conversion instead of skipping it;
- Document conversion is controlled by the `documentConversion` master switch (default `true`); when it is `false`, document renames are logged and skipped and the engine is never probed;
- Extensions entering the document pipeline: `.docx .docm .dotx .doc .odt .ott .rtf .md .markdown .html .htm .xhtml .pptx .pptm .potx .ppt .odp .otp .xlsx .xlsm .xltx .xls .ods .ots .csv .tsv` (`.pdf` is a source only when `allowPdfSource` is `true`). `.txt` is deliberately **excluded**: renaming a text file to something else is a common accident and plain text has no magic number to verify, so admitting it would only push files whose content cannot be confirmed into the pipeline;
- Conversion stays inside one family (the complete matrix is in "8. Supported Formats"): Writer family sources produce `docx / doc / odt / ott / rtf / md / markdown / html / htm / xhtml / pdf`, Impress family sources produce `pptx / ppt / odp / otp / pdf` and Calc family sources produce `xlsx / xls / ods / ots / csv / tsv / pdf`; cross-family conversion is allowed only with `pdf` as the target, and any other cross-family combination is refused by design with a clear reason (renaming `a.docx` to `a.xlsx` logs one SKIPPED line whose reason names csv / pdf as the sanctioned escapes for spreadsheets);
- Macro-enabled inputs (`.docm` / `.xlsm` / `.pptm`) are accepted as sources but never produced as targets - the program does not generate macro-enabled documents;
- PDF as a source is off by default (`allowPdfSource: false`): PDF import only guarantees text content and loses columns, floating objects and table boundaries, so it is kept out of the pipeline unless the user opts in;
- `md → docx` is handled by a **built-in converter** that needs no external engine and supports headings, bold and italic, inline code, fenced code blocks, links, images, blockquotes, ordered and unordered lists, tables, horizontal rules and paragraph breaks. The losses are explicit: no Word-level styles or themes, no reference-link resolution, no footnotes and no nested-list indentation fidelity. Markdown output (for example `docx → md`) goes through the external engine and is markdown-ish, keeping the basic structure only;
- The engine route is selected by `documentEngine` out of three engines: `auto` (the default) tries the **bundled engine directory `LibreOffice\` next to the executable** first, then a system-installed LibreOffice (headless `soffice`), then Microsoft Office COM (Word / PowerPoint / Excel); `libreoffice`, `bundled` and `com` allow exactly one of them each (`bundled` = the directory next to the exe). The all-in-one release ships `LibreOffice\`, so it works out of the box. The `RENAMEPRO_SOFFICE` environment variable can point at any soffice (used by tests and portable deployments, highest priority except in `com` mode) and `RENAMEPRO_DOCENGINE` relocates the bundled directory;
- LibreOffice is always started headless with a private per-process profile (`-env:UserInstallation=file:///...`). **Why the private profile matters**: without it a second `soffice` hands the job to an already-running instance and exits 0 while the real conversion is still running (a false success), and two processes sharing one profile can corrupt it;
- Documents run on their own serialized scheduler lane (`docMaxConcurrency`, default 1), so two engines never race;
- When no engine is available the behaviour is exactly the FFmpeg-missing behaviour: document conversion alone is disabled, one warning block with a concrete reason is written to `log.txt`, every document rename logs one SKIPPED line, and images and audio/video are completely unaffected; the built-in `md → docx` converter needs no engine at all and keeps working even when everything else is unavailable;
- Document types are verified by magic number before conversion, exactly like images and audio/video, so a `.docx` that is really a PNG is skipped and never converted. All OOXML formats are ZIP containers and are told apart by their entry list: an entry with a `word/` prefix means Word, `ppt/` means PowerPoint and `xl/` means Excel (a ZIP keeps its entry names in the central directory at the end of the file, so this is a real entry listing, not a guess at the file header); ODF (odt / ods / odp) is identified by the contents of its stored `mimetype` entry; legacy `.doc` / `.xls` / `.ppt` by the OLE2 signature plus the root directory entry name; RTF by `{\rtf` and PDF by `%PDF-`;
- Plain-text sources (`.md` / `.markdown` / `.csv` / `.tsv` / `.html` / `.htm` / `.xhtml`, `.txt` excluded) cannot have a magic number, so they take a deliberately conservative path: the file is accepted only when its bytes match no known binary format at all (and, for HTML, when it carries an HTML marker) - any recognizable binary signature rejects it. Verification always fails closed: a file that cannot be verified is skipped, never converted.

### Backup and Failure Handling

- Before converting, an original-format copy is created; the separator follows the system UI language (`a - 副本.jpg` in Chinese, `a - copy.jpg` in English) and name conflicts grow as `a - 副本 (2).jpg`;
- The copy lives next to the target file and is **never deleted automatically** by the program;
- Special files are skipped: EFS-encrypted files and cloud on-demand placeholders (`skipCloudFiles`); read-only targets have the read-only bit cleared temporarily and restored after the replacement;
- File-sharing violations (IOException) are retried up to three times with a 500 ms interval; a task that still fails is reported as failed and leaves no half-finished state (temporary files removed, target file untouched);
- Conservative default: failures are logged and nothing is rolled back. With `autoRollbackOnFailure` enabled, a failure deletes the target file and moves the copy back to its original name - the internal-operation suppression table is registered before the move, so the restored file is never converted again.

### Progress and Notifications

- The progress dialog wraps the Shell COM interface `IOperationsProgressDialog` directly, so its look and feel matches the File Explorer transfer dialog;
- Shell COM requires STA and a message loop, therefore a **dedicated STA message thread** is maintained and every dialog operation (create, update, destroy) is marshalled there through `Invoke / BeginInvoke`;
- Aggregated display: one dialog per batch, combining the number of finished tasks with the progress of the current task; when the total is unknown (stream copy without a known duration) the dialog switches to marquee mode;
- Tasks that finish within 400 ms do not open a dialog at all (anti-flicker) and complete silently;
- Cancellation: pressing Cancel in the dialog cancels the whole queue - the running FFmpeg process tree is terminated, copies are kept and the target file is never left broken;
- When a batch finishes, an optional Windows Toast summarizes the result (`enableToast`); failed entries use a "failure" prefix line, and if Toast is unavailable the tray icon flashes instead;
- Any Shell COM failure **degrades gracefully** to silent conversion plus a completion Toast and is logged - a broken UI component can never break the conversion itself.

### Reliability and Resource Usage

- Single instance enforced by a named mutex: launching the executable again only shows a notice and does not create a second tray icon;
- The tray icon is re-created automatically when Explorer restarts or when the notification area was not ready during early logon;
- Autostart is registered through Task Scheduler (run at logon) and the task target path is corrected automatically when the executable moves;
- Magick.NET is loaded lazily: the Magick assemblies and native library are loaded only when an image conversion actually runs, never during startup;
- Magick memory usage is capped at 512 MB so that huge images cannot starve the system;
- Measured idle footprint: about 57 MB working set and about 12 MB private bytes with Magick not loaded.

### Portability and Configuration

- Green and portable: unzip and double-click `RenamePro.exe`; configuration and logs live in the same folder as the executable;
- `config.json` is generated on first run with comments, can be edited in Notepad, and the tray menu entry for reloading the configuration applies the changes immediately (a couple of options need a restart).

## 3. Tech Stack

| Area | Choice and notes |
| --- | --- |
| Language / runtime | C# 12 / .NET 8, target framework `net8.0-windows10.0.19041.0` (win-x64) |
| UI | WinForms, but only `NotifyIcon` and `ContextMenuStrip` are used; no main window and no console (`OutputType=WinExe`) |
| Image conversion | Magick.NET-Q16-AnyCPU (**the only third-party NuGet package allowed in this project**), with percentage progress reported through the Magick progress event |
| Audio / video | FFmpeg: `ffmpeg.exe` is invoked as a child process, `ffprobe.exe` performs probing; progress is parsed from `-progress pipe:1` and errors are read from `stderr` |
| Document conversion | Three engine routes: the bundled engine directory `LibreOffice\` next to the executable (shipped with the all-in-one build, a trimmed official install), a system-installed LibreOffice (headless `soffice` with a private profile), and Microsoft Office COM (Word / PowerPoint / Excel); plus a built-in Markdown-to-docx converter that needs no engine |
| File watching | `FileSystemWatcher` (`FileName` notifications only plus a 256 KB buffer) combined with a hand-written 300 ms debouncer |
| System integration | Shell COM `IOperationsProgressDialog` (hand-written interop including `IShellItem`), WinRT Toast (`Windows.UI.Notifications`), Task Scheduler (`schtasks`), named mutex, Explorer broadcast message `TaskbarCreated`, Shell property store (writing the AUMID), child processes |
| Concurrency | `Task` plus `SemaphoreSlim` lane queues, `CancellationTokenSource` cancellation, a dedicated STA message thread for marshalling, `ConcurrentDictionary` for the re-entry guard and the suppression table |
| Configuration and logging | `System.Text.Json` (tolerant parsing that allows comments and trailing commas) and a thread-safe append-only log |
| Packaging | Image / audio-video builds: single-file self-contained publish (in-bundle compression enabled, ReadyToRun disabled, PublishTrim not used). All-in-one build: a single-file exe plus a sibling `LibreOffice\` folder. Green ZIP archives either way |

Design constraints: `PublishTrim` is never used (it breaks COM interop and reflection paths), `ReadyToRun` is disabled (it doubles the output size) and no third-party package other than Magick.NET is referenced.

### Release size breakdown (measured)

`RenamePro.csproj` strips WPF, designer and debug-symbol assemblies at publish time and `EnableCompressionInSingleFile` deflates the bundle contents; FFmpeg is a trimmed build compiled by this project and **embedded into the executable** (see below); since v1.2 the document engine travels as a **bundled directory** instead (see below).

| Artifact | Size | Notes |
| --- | --- | --- |
| `RenamePro.exe` (image / audio-video / all-in-one share one build) | 69.8 / 83.2 / 83.2 MB | the 83.2 MB one carries a 13.4 MB embedded FFmpeg payload; the runtime, WinForms and the native Magick.NET libraries all live inside |
| `RenamePro-图片版-v1.2.0.zip` (image) | 64.0 MB | **contains only `RenamePro.exe` + the readme**, image conversion only |
| `RenamePro-音视频版-v1.2.0.zip` (audio/video) | 77.4 MB | also just two files, images + audio/video (the embedded FFmpeg is extracted once) |
| `RenamePro-全功能版-v1.2.0.zip` (all-in-one) | 328.7 MB | **a folder package**: `RenamePro.exe` + `LibreOffice\` + readme, 4549 files / 787 MB inside; about 790 MB unpacked |
| `ffmpeg\` folder (local build input) | 34.4 MB | 2 executables + 10 runtime DLLs; it was the official essentials build before (201 MB for the two executables) |
| `libreoffice\` folder (local build input, becomes `LibreOffice\` in the package) | 703 MB | official LibreOffice 25.8.7 trimmed to 4547 files / 4546 manifest entries; the raw install tree is 1502 MB / 19673 files |
| Repository sources and icon | about 350 KB | binaries, DLLs, engine and packaging output are never committed |

The cost of in-bundle compression is the **first launch**, which extracts the bundle into `%TEMP%\.net\RenamePro\<hash>\` (once per version, then the cache is reused): measured cold start is about 1.3-1.9 s and a warm start about 30 ms. To opt out, remove `EnableCompressionInSingleFile` from `RenamePro.csproj` and you are back to an uncompressed single file.

### How a single file is possible (the FFmpeg payload)

The image and audio/video packages unpack to a single `RenamePro.exe`. FFmpeg is not compiled in; it travels like this:

1. `publish.ps1` compresses everything in `ffmpeg\` (2 executables + 10 DLLs) into **one** `Assets\ffmpeg-payload.zip` (about 13.4 MB);
2. that ZIP is written into the assembly at **compile time** as an `EmbeddedResource` (the C# compiler handles this, independently of the single-file bundler, so no `ExcludeFromSingleFile` is involved);
3. at runtime [RuntimePayload.cs](Conversion/RuntimePayload.cs:1) lazily extracts it **on the first audio/video conversion** into
   ```
   %LOCALAPPDATA%\RenamePro\runtime\<payload-id>\
   ```
   where the payload id is the first 16 hex digits of the payload's SHA256, so upgrading FFmpeg switches directory and the old one is cleaned up in the background;
4. [AvConverter](Conversion/AvConverter.cs:88) invokes `ffmpeg.exe` / `ffprobe.exe` from that directory.

**Why it has to touch the disk**: the Windows loader only loads modules from disk paths. `ffmpeg.exe` is a child process started with `CreateProcess`, and the 10 DLLs it needs are located by the loader in the executable's own directory; embedded resources are invisible to the loader. "Not a single file on disk" is impossible on Windows — "the user receives a single file" is not.

Measured behaviour:

- extracting the 34.4 MB payload takes **about 150 ms** (once per payload, reused from then on);
- extraction happens on the **first audio/video conversion**, never on the startup path: image-only users never extract anything, and tray startup matches the image-only build;
- if the preferred directory is not writable the payload falls back to `%TEMP%\RenamePro\runtime\<payload-id>\`;
- if both fail, or a security product quarantines an extracted DLL, audio/video is disabled with the reason written to `log.txt`, and **image conversion is unaffected**;
- when `ffmpeg.exe` / `ffprobe.exe` sit next to the executable (a developer `dotnet build` output directory) they are used directly and nothing is extracted.

> The three builds must come from **separate publishes**: the FFmpeg payload is embedded at compile time, so one publish can never be both. `publish.ps1 -Mode All` already does this in order (image build first, then the payload-carrying audio/video and all-in-one builds).

### Why the document engine is a directory, not an embedded payload (the v1.2 trade-off)

An earlier design also compressed the trimmed LibreOffice into a payload, embedded it into the executable at compile time and extracted it to `%LOCALAPPDATA%\RenamePro\runtime\doc-<payload-id>\` on the first document conversion. The measured costs outweighed the benefits, so v1.2 ships it as a directory next to the exe:

| | Embedded payload (v1.1) | Bundled directory (v1.2) |
| --- | --- | --- |
| All-in-one executable | 338.8 MB | 83.1 MB |
| First document conversion | extracts 722 MB into `%LOCALAPPDATA%` first (tens of seconds, and another copy on the system drive) | the directory is already there - **no extraction step at all** |
| Disk footprint | 339 MB exe + 722 MB extracted copy | 83 MB exe + 703 MB directory |
| Failure modes | extraction can be blocked by a security product or run out of space, and document conversion is then unavailable | only "the folder was deleted / the exe was copied away", and the log names the path |

The shape now: `LibreOffice\` (`program\` / `share\` / `Fonts\` / `licenses\` / `payload.json`) sits next to `RenamePro.exe`, and [DocumentEngine.cs](Conversion/DocumentEngine.cs:1) resolves the engine in the order `RENAMEPRO_SOFFICE` → bundled directory (`RENAMEPRO_DOCENGINE` overrides it) → system install → Office COM. Whether a candidate works is **always decided by a `soffice --version` self-check**, so a missing DLL, a quarantined file or a half-copied directory surfaces there instead of on the user's first conversion. See [build-docengine.ps1](build-docengine.ps1:1) and [docs/测试方法.md](docs/测试方法.md:1) (Chinese).

One thing users need to remember: **`RenamePro.exe` must stay next to `LibreOffice\`**; copying the exe out on its own degrades to "system LibreOffice / Office COM only".

### Trimmed FFmpeg (`build-ffmpeg.ps1`)

Official builds (gyan.dev essentials / full, BtbN) carry a large amount of components and every hardware-acceleration entry this program never calls: the two executables unpack to 201 MB, while the project only needs

- encoders: `libx264` `aac` `libmp3lame` `libvorbis` `libopus` `libvpx-vp9` `flac` `pcm_s16le` `wmv2` `wmav2` `mpeg4`
- containers: mp4/mov/m4a, mkv, webm, avi, wmv(asf), flv, ts, ogg, mp3, wav, flac
- probing: `ffprobe -show_entries stream=codec_type,codec_name / format=duration -of json`

So [build-ffmpeg.ps1](build-ffmpeg.ps1:1) runs `--disable-everything` and whitelists exactly those; the result is **12.8 MB / 12.6 MB** plus 10 runtime DLLs. The DLLs are required because the UCRT64 x264/lame/opus/vorbis/vpx/iconv/zlib libraries are linked through import libraries (`-Wl,-Bstatic` cannot reach those positions in FFmpeg's link line), so they must sit next to the executables or the binary fails silently with exit code `0xC0000135`.

After changing the whitelist you **must** run the verifier. It covers 81 checks (55 media capability and conversion smoke checks + 26 payload consistency checks):

```powershell
winget install --id MSYS2.MSYS2 -e            # one-off: install MSYS2
# install the toolchain inside the MSYS2 UCRT64 shell (the command is printed by build-ffmpeg.ps1 on failure)
powershell -ExecutionPolicy Bypass -File build-ffmpeg.ps1 -Proxy http://127.0.0.1:7897   # 20-40 minutes
powershell -ExecutionPolicy Bypass -File verify-ffmpeg.ps1                # verify the ffmpeg\ folder
powershell -ExecutionPolicy Bypass -File publish.ps1 -Mode Av            # build the FFmpeg payload and package
powershell -ExecutionPolicy Bypass -File verify-ffmpeg.ps1 -FromPayload   # verify the payload that gets embedded
```

`-FromPayload` unpacks `Assets\ffmpeg-payload.zip`, runs every media check against it and asserts that the payload's file list and SHA256 hashes match the `ffmpeg\` folder exactly. That is the guard against "edited `ffmpeg\` but forgot to regenerate the payload".

Upgrading FFmpeg is just `build-ffmpeg.ps1 -Version <x.y.z>`: it re-clones that tag and reuses the whitelist. Always re-run both verification steps before packaging a new version.

## 4. How It Works

```
Extension changed in File Explorer
        |
        v
FileSystemWatcher.Renamed (all fixed drives, can be limited by watchDrives; file-name notifications only)
        |
        +- internal-operation suppression table hit? -> drop the event (change caused by this program)
        +- extension whitelist (normalized extensions differ and stay inside one media category)
        +- excluded directories (system folders, Recycle Bin, Volume Shadow Copy data)
        +- excluded file names (~$, dot-prefixed)
        |
        v
300 ms debounce (only the last rename of a path is processed)
        |
        v
Pipeline: re-entry guard -> special-file checks -> magic-number check -> media probing (ffprobe) / document matrix decision
        |
        +- fast lane (concurrency 2): FFmpeg stream copies, images smaller than 2 MB
        +- image lane (concurrency = imageConcurrency): Magick.NET bitmap conversion
        +- audio/video lane (concurrency 1): FFmpeg tasks that need re-encoding
        +- document lane (concurrency = docMaxConcurrency, default 1): headless LibreOffice, the embedded
           engine or Office COM, converting inside one family or to pdf
        |
        +- no document engine available? -> document conversion alone is disabled (one warning plus one
           SKIPPED line per document rename); images and audio/video are unaffected
        |
        v
Create the original-format copy -> convert into a temporary file -> register the suppression table
-> atomic File.Replace
        |
        v
Result written to log.txt (OK / FAILED / CANCELLED / SKIPPED plus error details)
```

## 5. Project Layout

```
RenamePro/
├── Assets/                Application icon (used both as the exe icon and as an embedded resource)
├── Core/                  Configuration, logging, path rules, file-header sniffing, IO retry, autostart, single instance
├── Watching/              FileSystemWatcher wrapper, 300 ms debounce scheduler
├── Conversion/            Pipeline, tiered scheduler, backup manager, image converter, audio/video converter, payload manager
│                          (documents: DocConverter.cs, DocumentEngine.cs, DocumentMatrix.cs, MarkdownToDocx.cs)
├── Interop/               Shell COM progress dialog declarations and wrapper, IShellItem / AUMID helpers
├── Progress/              Progress coordinator, dedicated STA message thread, Toast service
├── Tray/                  Tray application context, Shell broadcast message window
├── ffmpeg/                ffmpeg.exe, ffprobe.exe and 10 runtime DLLs (build input, not tracked)
├── libreoffice/           Bundled engine directory (build-docengine.ps1 output, untracked; becomes LibreOffice\ inside the package)
├── Assets/ffmpeg-payload.zip  Those ffmpeg files compressed into one payload (build artifact, untracked, embedded at compile time)
├── app.manifest           Windows 10/11 compatibility declaration and DPI settings
├── RenamePro.csproj       Project file (size target, FFmpeg payload embedding, dev-time ffmpeg/libreoffice copy rules)
├── build-ffmpeg.ps1       Trimmed FFmpeg build script (whitelist configure + runtime DLLs)
├── verify-ffmpeg.ps1      FFmpeg verifier (55 capability and conversion smoke checks + 26 payload consistency checks)
├── setup-lo-dev.ps1       Document engine dev setup (download the official MSI, unpack it; no install, no COM registration)
├── build-docengine.ps1    Bundled engine build script (trim + per-category smoke rollback + payload.json manifest)
├── verify-docengine.ps1   Document conversion end-to-end verifier (-Soffice / -EngineDir / -Package)
├── test/stub-engine/      Stub engine (a soffice test double, never shipped)
├── docs/测试方法.md        Document conversion test guide (Chinese)
└── publish.ps1            Portable packaging script (FFmpeg payload, three builds, ZIP archives)
```

## 6. Getting Started

### Option 1: Use a release package (recommended)

1. Download from the Releases page (three builds):
   - `RenamePro-图片版-*.zip` (image): smallest, about 64 MB, image conversion only; audio/video and document renames are skipped with a log entry;
   - `RenamePro-音视频版-*.zip` (audio/video): images plus audio/video, FFmpeg embedded, about 78 MB;
   - `RenamePro-全功能版-*.zip` (all-in-one): adds document conversion; **a folder package** (`RenamePro.exe` + `LibreOffice\`), about 330 MB;
2. Unzip anywhere: the image and audio/video builds contain **a single `RenamePro.exe`** (plus the readme), the all-in-one build contains `RenamePro.exe` + `LibreOffice\` + the readme. Double-click the exe and only a tray icon appears;
3. Rename files in File Explorer as usual, for example `photo.jpg` to `photo.png`;
4. Tray icon context menu: pause/resume watching, autostart at logon, reload configuration, document engine status, exit.

The first run creates `config.json` and `log.txt` next to the executable. Registering the autostart task raises a single UAC prompt; accepting it is enough.

The first audio/video conversion in the audio/video and all-in-one builds extracts the embedded FFmpeg into `%LOCALAPPDATA%\RenamePro\runtime\` (about 150 ms, once); image-only usage never triggers it. **The document engine has no extraction step**: the `LibreOffice\` folder is already there, just keep it next to the exe.

### Option 2: Build from source

```powershell
# Requirements: .NET 8 SDK, Windows 10/11 x64
git clone https://github.com/Ha2eS1onn/RenamePro.git
cd RenamePro
# Audio/video support needs FFmpeg: build the trimmed one with build-ffmpeg.ps1 (recommended),
# or drop any ffmpeg.exe / ffprobe.exe (plus their DLLs) into the ffmpeg\ folder
dotnet build RenamePro.csproj -c Debug
# Document conversion needs an engine (optional): unpack the official LibreOffice, then trim it
powershell -ExecutionPolicy Bypass -File setup-lo-dev.ps1
powershell -ExecutionPolicy Bypass -File build-docengine.ps1 -ReuseStage
```

### Packaging portable releases

```powershell
# Build all three ZIPs (output goes to dist\ by default)
powershell -ExecutionPolicy Bypass -File publish.ps1 -Mode All -Version 1.2.0
# Build a single package
powershell -ExecutionPolicy Bypass -File publish.ps1 -Mode Image   # image
powershell -ExecutionPolicy Bypass -File publish.ps1 -Mode Av      # audio/video
powershell -ExecutionPolicy Bypass -File publish.ps1 -Mode Docs    # all-in-one (needs libreoffice\)
```

`-Mode Docs` runs `build-docengine.ps1` automatically when `libreoffice\program\soffice.com` is missing; pass `-SkipEngineBuild` when the tree is ready. The all-in-one ZIP compresses a 700 MB folder (with 7-Zip when available) and takes a few minutes.

> Both scripts are saved as UTF-8; when running them with `powershell.exe` (Windows PowerShell 5.1) make sure they are decoded as UTF-8, otherwise their non-ASCII comments are read as ANSI and the parser reports syntax errors (the scripts in this repository ship with a BOM, so this normally does not happen). PowerShell 7 is unaffected.

## 7. Configuration (config.json)

The file is generated next to the executable on first run, contains comments, can be edited in Notepad, and the tray menu entry for reloading the configuration applies changes immediately (`imageConcurrency` takes effect on the next start when the queue is busy).

| Option | Default | Description |
| --- | --- | --- |
| `enableBackup` | `true` | Create an original-format copy before converting (`a - copy.jpg`); keeping it enabled is recommended |
| `autoRollbackOnFailure` | `false` | On failure, delete the target file and move the copy back to its original name |
| `enableToast` | `true` | Show a Windows Toast summary after a batch finishes |
| `showProgressDialog` | `true` | Show the system progress dialog; when `false`, Shell COM is never initialized and conversions run silently |
| `imageQuality` | `92` | JPEG output quality (1-100, applies to jpg output only) |
| `gifPolicy` | `"first-frame"` | GIF source policy: `first-frame` converts the first frame, `skip` ignores the rename |
| `imageConcurrency` | `2` | Image lane concurrency (1-8) |
| `documentConversion` | `true` | Master switch for document conversion; when `false`, document renames are logged and skipped and the engine is never probed |
| `documentEngine` | `"auto"` | Document engine route: `auto` (bundled `LibreOffice\` directory, then a system LibreOffice, then Office COM), `libreoffice` (system only), `bundled` (directory only) or `com` (Office only) |
| `allowPdfSource` | `false` | Allow pdf as a conversion source (off by default, see "11. Known Limitations") |
| `docTimeoutSeconds` | `180` | Per-task timeout in seconds (30-1800); the effective timeout additionally grows by 3 s per MB of source file, capped at 1800 s |
| `docMaxConcurrency` | `1` | Document lane concurrency (1-4); keeping it at 1 guarantees that only one document engine runs at a time |
| `docComRetries` | `3` | Retries for transient Office COM rejections such as a busy RPC channel (0-5) |
| `docWarmupOnStart` | `true` | Warm the engine once in the background at startup, paying LibreOffice's first-run profile cost outside the user's first conversion |
| `watchDrives` | `[]` | Drive list to watch, for example `["D:\\", "E:\\"]`; empty means all fixed drives |
| `skipCloudFiles` | `true` | Skip cloud on-demand placeholder files (RecallOnDataAccess) |

Out-of-range values are clamped (`imageQuality` 1-100, `imageConcurrency` 1-8, `docTimeoutSeconds` 30-1800, `docMaxConcurrency` 1-4, `docComRetries` 0-5) and an unrecognised `documentEngine` is treated as `auto`. A configuration that cannot be parsed falls back to the defaults and is logged; the program keeps running.

## 8. Supported Formats

Images (converted within the same category):

| Source | Target | Handling |
| --- | --- | --- |
| png / jpg / jpeg / bmp / gif / webp / tiff | png / jpg / jpeg / bmp / gif / webp / tiff | Magick.NET conversion; jpg output is lossy (quality configurable), webp output is lossless, gif sources follow `gifPolicy` (first frame) |
| Any combination involving `.ico` | - | Silently skipped (log entry only) |

Audio and video:

| Scenario | Handling |
| --- | --- |
| Video container conversion (mp4 / mkv / avi / mov / wmv / ts / webm / flv) | `-c copy` stream copy when every codec is in `h264 / hevc / aac / ac3 / opus / vorbis / mp3` and the target container accepts it; otherwise re-encoded |
| Audio conversion (mp3 / wav / flac / aac / ogg / m4a) | Encoder chosen by target format (see above); lossy-to-lossless is allowed with a log note |
| Video to audio | Extract the audio track and encode it to the target audio format |
| Audio to a video container | Mux the audio stream only, stream copy when possible |
| Combinations outside the supported matrix | Silently skipped (log entry, no prompt) |

### Documents

Documents convert inside one family (a same-family source and target, or pdf as the target):

| Source family | Source extensions | Targets |
| --- | --- | --- |
| Writer | docx / doc / docm / dotx / odt / ott / rtf / md / markdown / html / htm / xhtml | docx / doc / odt / ott / rtf / md / markdown / html / htm / xhtml / pdf |
| Impress | pptx / pptm / potx / ppt / odp / otp | pptx / ppt / odp / otp / pdf |
| Calc | xlsx / xlsm / xltx / xls / ods / ots / csv / tsv | xlsx / xls / ods / ots / csv / tsv / pdf |

(`.txt` is neither a source nor a target: it has no magic number to verify and is not part of the document set. The tables above and the scenario table below are the complete statement of what is supported. A Writer source renamed to `.csv` counts as cross-family and is refused.)

| Scenario | Handling |
| --- | --- |
| Same-family conversion | Performed by the selected engine; the result is written to a temporary file in the same folder and then atomically replaced |
| Cross-family conversion whose target is not pdf | Refused (SKIPPED) with a reason stating that document cross-family conversion only allows pdf as the target (spreadsheets have to go through csv / pdf) |
| Any family to pdf | Allowed - pdf is the only cross-family target |
| `.docm` / `.xlsm` / `.pptm` | Accepted as sources, never produced as targets (no macro-enabled documents are generated) |
| `.pdf` as a source | Off by default (`allowPdfSource: false`); when enabled it only guarantees text content and loses columns, floating objects and table boundaries |
| `md → docx` | Built-in converter, no external engine required |
| Markdown output such as `docx → md` | Goes through the external engine and is markdown-ish (basic structure only) |
| Content that does not match the extension | Skipped on a magic-number mismatch (log entry only) |

## 9. Logging

The log is written to `log.txt` next to the executable. Every conversion records the time, the old path, the new path, the result and error details. Log messages themselves are written in Chinese:

```
[2026-09-25 23:14:25.415] [INFO] [进度] 已打开系统进度对话框（资源管理器传输框同款）
[2026-09-25 23:14:29.010] [INFO] [结果] OK | 旧: D:\demo\rec.mkv | 新: D:\demo\rec.mp4 | 副本: D:\demo\rec - 副本.mkv | 耗时: 4.2s
[2026-09-25 23:25:26.423] [ERROR] [结果] FAILED | 旧: D:\demo\bad.jpg | 新: D:\demo\bad.png | 副本: D:\demo\bad - 副本.jpg | 错误: insufficient image data in file ...
[2026-09-25 23:25:26.427] [INFO] [回滚] 已恢复原名：D:\demo\bad.jpg（副本已移回，不会再被二次转换）
[2026-09-25 23:26:12.347] [WARN] [重试] 原子替换 第 2 次失败（文件被占用），500ms 后重试（最多 3 次）
[2026-09-25 23:31:02.114] [INFO] [文档] 引擎就绪：LibreOffice 25.8 (C:\Program Files\LibreOffice\program\soffice.com)
[2026-09-25 23:31:02.118] [INFO] [文档] 引擎就绪：内置 Markdown 转换器
[2026-09-25 23:31:05.482] [INFO] [结果] OK | 旧: D:\demo\report.docx | 新: D:\demo\report.pdf | 副本: D:\demo\report - 副本.docx | 耗时: 3.1s
[2026-09-25 23:31:06.117] [INFO] [结果] SKIPPED | 旧: D:\demo\fake.docx | 新: D:\demo\fake.pdf | 原因: magic 不符：内容实为 png，源扩展名 .docx
[2026-09-25 23:31:07.204] [INFO] [结果] SKIPPED | 旧: D:\demo\a.docx | 新: D:\demo\a.xlsx | 原因: 不在支持矩阵内：文档跨族转换只允许目标为 pdf（表格请改成 csv/pdf）
[2026-09-25 23:32:10.884] [ERROR] [结果] FAILED | 旧: D:\demo\thesis.docx | 新: D:\demo\thesis.odt | 副本: D:\demo\thesis - 副本.docx | 错误: 文档转换超时（180 秒，源文件 D:\demo\thesis.docx）
[2026-10-02 09:02:11.004] [INFO] [文档] 引擎就绪：Word 16.0 COM
[2026-10-03 09:02:14.500] [WARN] [文档] 引擎不可用：未找到 LibreOffice（已探测常见安装目录、注册表与 PATH），Office COM 不可用（未注册 Word.Application）
```

Result values: `OK` / `FAILED` / `CANCELLED` / `SKIPPED`. Skip reasons include "not the same media category", "extension unchanged", "inside an excluded directory", "magic number mismatch", "ffmpeg.exe missing" and "outside the supported matrix". When a watcher buffer overflows, the time window of the potentially lost events is recorded as well.

## 10. Development Milestones

The project was built in five milestones, each with its own commit and hands-on acceptance run:

| Milestone | Contents |
| --- | --- |
| 1 | Project skeleton, fixed-drive rename watching and filtering, tray icon and exit menu, autostart, pause watching |
| 2 | Backup manager, tiered scheduler (fast 2 / image 2 / audio-video 1), Magick.NET image converter and FFmpeg audio/video converter, pipeline integration |
| 3 | Dedicated STA message thread, `IOperationsProgressDialog` wrapper, aggregated progress and cancellation, Shell COM degradation with Toast fallback |
| 4 | `config.json` configuration, failure rollback, resource control (lazy Magick loading and memory cap), hardening, portable packaging script |
| 5 | Document conversion: headless LibreOffice / Office COM routes, conversion matrix with magic verification, built-in Markdown → docx, `--selftest` and `--docdiagnose` troubleshooting entries; v1.2 moved the engine from an embedded payload to a bundled `LibreOffice\` folder and split the release into the image / audio-video / all-in-one packages |

## 11. Known Limitations

- Windows 10 / 11 x64 only (Shell COM and WinRT components are required);
- On Windows 11 the notification-area icon may be folded into the hidden-icons flyout; pinning it there is a system setting the user has to change;
- Tasks expected to finish within 400 ms never show a progress dialog (anti-flicker behaviour by design);
- Skipping cloud on-demand placeholders is implemented but needs a real on-demand sync environment (such as OneDrive) to be verified end to end;
- Changing `imageConcurrency` in `config.json` requires a restart when the queue is busy;
- Renames involving `.ico` and combinations outside the supported matrix are silently ignored (log entry only, no prompt);
- Cross-family document conversion (any conversion whose target is not pdf) is refused by design and only logged as skipped;
- `docx → md` keeps the basic Markdown structure only, and `md → docx` does not carry Word-level styles or themes;
- PDF as a source is off by default, and when enabled it is text-only (columns, floating objects and table boundaries are lost);
- The document engine is a heavy external program, so the first conversion may take several seconds while the engine and its profile warm up;
- Office COM cannot run while the same document is open in Word / PowerPoint / Excel - close that document first;
- Office COM depends on the environment: under a sandbox, a service session or a restricted account, Word and Excel automation can fail outright (PowerPoint usually still works). When that happens, switch to LibreOffice (`documentEngine` set to `"libreoffice"`) or rule out Office automation on that machine;
- The `bundled` route only accepts the **bundled engine directory** (the `LibreOffice\` of the all-in-one build); the image and audio/video builds have no such directory, so that route is unavailable and the log names the path it looked at;
- The all-in-one build is a **folder package**: `RenamePro.exe` must be used together with the sibling `LibreOffice\`. Copying the exe out does not error out — it just degrades to "system LibreOffice / Office COM only";
- The program directory must be writable because `config.json` and `log.txt` live next to the exe, so placing the all-in-one build in `Program Files` is not recommended (the `LibreOffice\` folder itself needs no write access; LibreOffice's user profile is created under `%LOCALAPPDATA%\RenamePro\DocEngine`);
- A single-file exe (the main executable of every build) extracts its bundle into `%TEMP%\.net\RenamePro\<hash>\` on first launch: when `%TEMP%` is not writable (restricted account, a temp directory locked down by a security product) the process exits immediately with `Failed to create default extraction directory … error code: 5` and no `log.txt` is written at all. Point the `DOTNET_BUNDLE_EXTRACT_BASE_DIR` environment variable at a writable directory in that case; this is unrelated to the document engine, which is a plain directory and is never extracted;
- The bundled engine is a one-off copy of about 700 MB and 4500+ files, so the first conversion can be noticeably slower while a security product scans it; afterwards it is back to normal;
- Office COM automation requires a normal interactive desktop session.

## 12. Third-Party Components

| Component | Purpose | License notes |
| --- | --- | --- |
| Magick.NET / ImageMagick | Image format conversion | Apache-2.0 and the ImageMagick License (see the upstream repositories) |
| FFmpeg / ffprobe | Audio/video conversion and probing | GPL (any build containing libx264 is GPL); this repository does not contain the binaries, and the two executables in the release packages are compiled by [build-ffmpeg.ps1](build-ffmpeg.ps1:1) with `--enable-gpl` from a pinned tag, so the source and configure switches are reproducible; redistributing them requires complying with that license and attributing the source |
| Bundled runtime DLLs | x264 / lame / opus / vorbis / libvpx / libogg / libiconv / zlib / winpthread | Each follows its upstream license (GPL / LGPL / BSD and others); together with the two executables they are compressed into the payload embedded in the audio/video and all-in-one builds, extracted to the user directory at runtime, and are not tracked |
| Microsoft.Windows.SDK.NET | WinRT Toast projection | Provided by the .NET 8 target framework, no extra NuGet package required |
| LibreOffice | Document conversion engine (shipped as `LibreOffice\` in the all-in-one build; in the other builds it is used only when installed on the user's machine and selected by `documentEngine`) | MPL-2.0 (file-level copyleft, compatible with this project's GPL-3.0); this repository does not contain the binaries, the `LibreOffice\` in the all-in-one package is trimmed from the official installer by [build-docengine.ps1](build-docengine.ps1:1) and ships with the license and third-party notices under `LibreOffice\licenses\`; for source, take the matching official release (LibreOffice 25.8.7) |

## 13. License

This project is released under the **GNU General Public License v3.0 (GPL-3.0)**; the full text is available in the [LICENSE](LICENSE) file at the root of this repository.

- You may use, modify and redistribute this project freely, but any distributed derivative work must be licensed under GPL-3.0 as well and include the complete corresponding source code;
- The complete source code is already available in this repository, which satisfies the GPL-3.0 source-availability requirement;
- The `ffmpeg.exe` / `ffprobe.exe` shipped inside the release packages belong to the FFmpeg project and are governed by their own build license (builds that include libx264 and similar components are GPL); please comply with those terms and attribute the source when redistributing;
- Magick.NET / ImageMagick, used for image conversion, are released under Apache-2.0 and the ImageMagick License, which are compatible with this project's GPL-3.0.

When using this tool for batch conversions, make sure you are allowed to process the files you target.




