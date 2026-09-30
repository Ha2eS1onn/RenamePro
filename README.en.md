# RenamePro

[简体中文](README.md) | English

A Windows tray utility that converts file contents for real when you change a file extension in File Explorer (for example, renaming `a.jpg` to `a.png`).

It adds no shell context-menu entries, opens no custom main window and never shows a console window: after double-clicking the executable the only visible artifact is a tray icon, and from then on the program simply follows the renames you perform in File Explorer.

## 1. Overview

Changing a file extension on Windows does not change the file contents, so you often end up with a broken file whose name says PNG while its bytes are still JPEG (image viewers fail, media players report errors). RenamePro watches file rename events across the system, detects the case where "the extension changed and both the old and the new extensions belong to the same media category", and then does three things automatically:

1. Copies the renamed file byte for byte into an original-format copy (`a - 副本.jpg` on Chinese systems, `a - copy.jpg` on English systems) so that nothing is lost;
2. Converts the file contents into the new format, writes the result to a temporary file in the same folder and atomically replaces the renamed file;
3. Reports progress through the same system progress dialog that File Explorer uses, and the operation can be cancelled.

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
| File watching | `FileSystemWatcher` (`FileName` notifications only plus a 256 KB buffer) combined with a hand-written 300 ms debouncer |
| System integration | Shell COM `IOperationsProgressDialog` (hand-written interop including `IShellItem`), WinRT Toast (`Windows.UI.Notifications`), Task Scheduler (`schtasks`), named mutex, Explorer broadcast message `TaskbarCreated`, Shell property store (writing the AUMID), child processes |
| Concurrency | `Task` plus `SemaphoreSlim` lane queues, `CancellationTokenSource` cancellation, a dedicated STA message thread for marshalling, `ConcurrentDictionary` for the re-entry guard and the suppression table |
| Configuration and logging | `System.Text.Json` (tolerant parsing that allows comments and trailing commas) and a thread-safe append-only log |
| Packaging | Single-file self-contained publish (in-bundle compression enabled, ReadyToRun disabled, PublishTrim not used) driven by a PowerShell packaging script that produces green ZIP archives |

Design constraints: `PublishTrim` is never used (it breaks COM interop and reflection paths), `ReadyToRun` is disabled (it doubles the output size) and no third-party package other than Magick.NET is referenced.

### Release size breakdown (measured)

`RenamePro.csproj` strips WPF, designer and debug-symbol assemblies at publish time and `EnableCompressionInSingleFile` deflates the bundle contents; FFmpeg is a trimmed build compiled by this project and **embedded into the executable** (see below).

| Artifact | Size | Notes |
| --- | --- | --- |
| `RenamePro.exe` (full) | 83.1 MB | carries a 13.4 MB embedded FFmpeg payload; the runtime, WinForms and the native Magick.NET libraries all live inside |
| `RenamePro-Full-*.zip` | 77.5 MB | **contains only `RenamePro.exe` + the readme**, with full image and audio/video support |
| `RenamePro.exe` (image-only) | 69.7 MB | no FFmpeg payload; audio/video renames are logged and skipped |
| `RenamePro-Image-*.zip` | 64.2 MB | likewise only the executable + the readme |
| `ffmpeg\` folder (local build input) | 34.4 MB | 2 executables + 10 runtime DLLs; it was the official essentials build before (201 MB for the two executables) |
| Repository sources and icon | about 350 KB | binaries, DLLs, payload and packaging output are never committed |

The cost of in-bundle compression is the **first launch**, which extracts the bundle into `%TEMP%\.net\RenamePro\<hash>\` (once per version, then the cache is reused): measured cold start is about 1.3-1.9 s and a warm start about 30 ms. To opt out, remove `EnableCompressionInSingleFile` from `RenamePro.csproj` and you are back to an uncompressed single file.

### How a single file is possible (the FFmpeg payload)

The full package unpacks to a single `RenamePro.exe`. FFmpeg is not compiled in; it travels like this:

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

> The full and image-only builds must be **two separate publishes**: the payload is embedded at compile time, so one publish can never be both. `publish.ps1 -Mode Both` already does this in order (full build with the payload, then the payload is moved aside for the image-only build).

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
powershell -ExecutionPolicy Bypass -File publish.ps1 -Mode Both           # build the payload and package
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
Pipeline: re-entry guard -> special-file checks -> magic-number check -> ffprobe classification
        |
        +- fast lane (concurrency 2): FFmpeg stream copies, images smaller than 2 MB
        +- image lane (concurrency = imageConcurrency): Magick.NET bitmap conversion
        +- audio/video lane (concurrency 1): FFmpeg tasks that need re-encoding
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
├── Interop/               Shell COM progress dialog declarations and wrapper, IShellItem / AUMID helpers
├── Progress/              Progress coordinator, dedicated STA message thread, Toast service
├── Tray/                  Tray application context, Shell broadcast message window
├── ffmpeg/                ffmpeg.exe, ffprobe.exe and 10 runtime DLLs (build input, not tracked)
├── Assets/ffmpeg-payload.zip  Those files compressed into one payload (build artifact, untracked, embedded at compile time)
├── app.manifest           Windows 10/11 compatibility declaration and DPI settings
├── RenamePro.csproj       Project file (size target, payload embedding, dev-time ffmpeg copy rules)
├── build-ffmpeg.ps1       Trimmed FFmpeg build script (whitelist configure + runtime DLLs)
├── verify-ffmpeg.ps1      FFmpeg verifier (55 capability and conversion smoke checks + 26 payload consistency checks)
└── publish.ps1            Portable packaging script (payload, two publishes, ZIP archives)
```

## 6. Getting Started

### Option 1: Use a release package (recommended)

1. Download from the Releases page:
   - `RenamePro-Full-*.zip`: FFmpeg embedded, full image and audio/video support;
   - `RenamePro-Image-*.zip`: smallest package, image conversion only; audio/video renames are skipped with a log entry;
2. Unzip anywhere: the folder contains **a single `RenamePro.exe`** (plus the readme); double-click it and only a tray icon appears;
3. Rename files in File Explorer as usual, for example `photo.jpg` to `photo.png`;
4. Tray icon context menu: pause/resume watching, autostart at logon, reload configuration, exit.

The first run creates `config.json` and `log.txt` next to the executable. Registering the autostart task raises a single UAC prompt; accepting it is enough.

The first audio/video conversion in the full build extracts the embedded FFmpeg into `%LOCALAPPDATA%\RenamePro\runtime\` (about 150 ms, once); image-only usage never triggers it.

### Option 2: Build from source

```powershell
# Requirements: .NET 8 SDK, Windows 10/11 x64
git clone https://github.com/Ha2eS1onn/RenamePro.git
cd RenamePro
# Audio/video support needs FFmpeg: build the trimmed one with build-ffmpeg.ps1 (recommended),
# or drop any ffmpeg.exe / ffprobe.exe (plus their DLLs) into the ffmpeg\ folder
dotnet build RenamePro.csproj -c Debug
```

### Packaging portable releases

```powershell
# Build both the full and the image-only ZIP (output goes to dist\ by default)
powershell -ExecutionPolicy Bypass -File publish.ps1
# Build a single package, or set a version number
powershell -ExecutionPolicy Bypass -File publish.ps1 -Mode Image -Version 1.1.0
```

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
| `watchDrives` | `[]` | Drive list to watch, for example `["D:\\", "E:\\"]`; empty means all fixed drives |
| `skipCloudFiles` | `true` | Skip cloud on-demand placeholder files (RecallOnDataAccess) |

Out-of-range values are clamped (`imageQuality` 1-100, `imageConcurrency` 1-8). A configuration that cannot be parsed falls back to the defaults and is logged; the program keeps running.

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

## 9. Logging

The log is written to `log.txt` next to the executable. Every conversion records the time, the old path, the new path, the result and error details. Log messages themselves are written in Chinese:

```
[2026-09-25 23:14:25.415] [INFO] [进度] 已打开系统进度对话框（资源管理器传输框同款）
[2026-09-25 23:14:29.010] [INFO] [结果] OK | 旧: D:\demo\rec.mkv | 新: D:\demo\rec.mp4 | 副本: D:\demo\rec - 副本.mkv | 耗时: 4.2s
[2026-09-25 23:25:26.423] [ERROR] [结果] FAILED | 旧: D:\demo\bad.jpg | 新: D:\demo\bad.png | 副本: D:\demo\bad - 副本.jpg | 错误: insufficient image data in file ...
[2026-09-25 23:25:26.427] [INFO] [回滚] 已恢复原名：D:\demo\bad.jpg（副本已移回，不会再被二次转换）
[2026-09-25 23:26:12.347] [WARN] [重试] 原子替换 第 2 次失败（文件被占用），500ms 后重试（最多 3 次）
```

Result values: `OK` / `FAILED` / `CANCELLED` / `SKIPPED`. Skip reasons include "not the same media category", "extension unchanged", "inside an excluded directory", "magic number mismatch", "ffmpeg.exe missing" and "outside the supported matrix". When a watcher buffer overflows, the time window of the potentially lost events is recorded as well.

## 10. Development Milestones

The project was built in four milestones, each with its own commit and hands-on acceptance run:

| Milestone | Contents |
| --- | --- |
| 1 | Project skeleton, fixed-drive rename watching and filtering, tray icon and exit menu, autostart, pause watching |
| 2 | Backup manager, tiered scheduler (fast 2 / image 2 / audio-video 1), Magick.NET image converter and FFmpeg audio/video converter, pipeline integration |
| 3 | Dedicated STA message thread, `IOperationsProgressDialog` wrapper, aggregated progress and cancellation, Shell COM degradation with Toast fallback |
| 4 | `config.json` configuration, failure rollback, resource control (lazy Magick loading and memory cap), hardening, portable packaging script |

## 11. Known Limitations

- Windows 10 / 11 x64 only (Shell COM and WinRT components are required);
- On Windows 11 the notification-area icon may be folded into the hidden-icons flyout; pinning it there is a system setting the user has to change;
- Tasks expected to finish within 400 ms never show a progress dialog (anti-flicker behaviour by design);
- Skipping cloud on-demand placeholders is implemented but needs a real on-demand sync environment (such as OneDrive) to be verified end to end;
- Changing `imageConcurrency` in `config.json` requires a restart when the queue is busy;
- Renames involving `.ico` and combinations outside the supported matrix are silently ignored (log entry only, no prompt).

## 12. Third-Party Components

| Component | Purpose | License notes |
| --- | --- | --- |
| Magick.NET / ImageMagick | Image format conversion | Apache-2.0 and the ImageMagick License (see the upstream repositories) |
| FFmpeg / ffprobe | Audio/video conversion and probing | GPL (any build containing libx264 is GPL); this repository does not contain the binaries, and the two executables in the release packages are compiled by [build-ffmpeg.ps1](build-ffmpeg.ps1:1) with `--enable-gpl` from a pinned tag, so the source and configure switches are reproducible; redistributing them requires complying with that license and attributing the source |
| Bundled runtime DLLs | x264 / lame / opus / vorbis / libvpx / libogg / libiconv / zlib / winpthread | Each follows its upstream license (GPL / LGPL / BSD and others); together with the two executables they are compressed into the payload embedded in the full build, extracted to the user directory at runtime, and are not tracked |
| Microsoft.Windows.SDK.NET | WinRT Toast projection | Provided by the .NET 8 target framework, no extra NuGet package required |

## 13. License

This project is released under the **GNU General Public License v3.0 (GPL-3.0)**; the full text is available in the [LICENSE](LICENSE) file at the root of this repository.

- You may use, modify and redistribute this project freely, but any distributed derivative work must be licensed under GPL-3.0 as well and include the complete corresponding source code;
- The complete source code is already available in this repository, which satisfies the GPL-3.0 source-availability requirement;
- The `ffmpeg.exe` / `ffprobe.exe` shipped inside the release packages belong to the FFmpeg project and are governed by their own build license (builds that include libx264 and similar components are GPL); please comply with those terms and attribute the source when redistributing;
- Magick.NET / ImageMagick, used for image conversion, are released under Apache-2.0 and the ImageMagick License, which are compatible with this project's GPL-3.0.

When using this tool for batch conversions, make sure you are allowed to process the files you target.




