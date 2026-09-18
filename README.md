# FootageReviewer

A fast, keyboard-driven desktop tool for reviewing **dozens of hours** of OBS gameplay footage — much quicker than scrubbing through Premiere. It plays a whole session's worth of clips as one continuous timeline, auto-transcribes each audio track, and lets you drop timestamped log notes as you watch.

> Windows desktop app · .NET 8 · Avalonia 11.3 · embedded **libmpv** · local **faster-whisper** transcription.

Built for a specific workflow: OBS recordings with **3 audio tracks** — Track 1 *gameplay* (rarely used), Track 2 *mic*, Track 3 *discord + alerts*.

---

## What it does

- **Continuous timeline from many clips.** Point it at a folder of recordings; they're laid out end-to-end as one virtual timeline (no 24-hour cap), played through libmpv via an `edl://` source.
- **Filmstrip thumbnails + per-track waveforms** under the video, generated in the background (with a little loading spinner on each clip while it works) and cached to disk + embedded in the project.
- **Manual logs.** Press **T** (or just start typing) to drop a timestamped note; entries are grouped into collapsible per-clip folders, searchable, colour-codable, mergeable, and exportable with **Copy all**. An optional **new-log offset** records each note a second or two before you pressed log.
- **Auto transcript** (local Whisper on the GPU) — one column per audio track, time-positioned next to the video timeline. Click a bubble to seek; copy a bubble into the log (consecutive copies from the same track merge into one quote). Generation can be **paused / stopped** and resumes from the cache on restart.
- **Editable clip sequence.** Drag clips in from Explorer, reorder them (right-click to select, then drag), delete, or **order chronologically** by the OBS filename timestamp. Logs, sync points and the playhead follow their clip. **Ctrl+Z / Ctrl+Shift+Z** undo/redo clip edits.
- **On-screen-timer sync** for speedruns: anchor the timeline to what an in-game timer reads, with multiple sync points across pauses.
- **Dictation** (press **D**): record the mic and transcribe it straight into a log in the background.
- **Markers** on both timelines (draggable to retime their log), playhead centre-lock, zoom lock between timelines, adjustable playback speed (incl. <1×), auto loudness levelling per track, autocorrect + spell-check in the log box.
- **Send a section to Premiere.** **X** opens a selection band on the playhead, the wheel resizes it, **C**
  copies it — an FCP7 XML sequence for that exact range lands on the clipboard, so pasting into Premiere's
  Project panel imports it with your original footage relinked at the right in/out points.
- **Home screen on launch** — every project you've worked on, with a poster frame, clip count, duration, log
  count and last-opened time; open one, or start a new one. Reopening your last project on launch is an
  opt-in checkbox. **File ▸ Home** comes back here.
- **Projects** (`.frproj`) with autosave + rotating versioned backups, recent-projects menu, and rename-in-place.

## Build & run

Requires the **.NET 8 SDK** (Windows). Native `libmpv-2.dll` + `ffmpeg`/`ffprobe` live under `native/win-x64/` (not in the repo — see below); transcription uses a local Python + faster-whisper configured under `native/whisper/`: copy `engine.example.json` to `engine.json` (macOS: `engine.macos.example.json` to `engine.macos.json`) and point `python` at an interpreter with the sidecar's requirements installed. The real `engine*.json` files are git-ignored because they hold a machine-specific absolute path.

```sh
# debug
dotnet run -c Release --project src/FootageReviewer.App/FootageReviewer.App.csproj

# self-contained-ish single-file publish (native deps land loose beside the exe)
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  src/FootageReviewer.App/FootageReviewer.App.csproj -o dist
# then run dist/FootageReviewer.exe  (launcher: "Launch FootageReviewer.cmd")
```

The repo is **source-only** (`.gitignore` excludes `dist/`, `native/win-x64/`, `bin`/`obj`, `*.frproj`). A clone needs the .NET 8 SDK plus the native libmpv/ffmpeg binaries to run.

## Project file (`.frproj`)

GZip-compressed JSON (magic `0x1F 0x8B`; legacy plain JSON still loads). Holds the ordered source paths, manual logs, sync points/offsets, playhead + zoom/scroll + lock states, per-track audio settings, and **embedded assets** — thumbnails (base64 JPEG), waveforms (8-bit quantized), and the raw transcript — so a project is self-contained and portable.

## Cache & data locations

- **Asset cache:** `%LOCALAPPDATA%\FootageReviewer\cache\` → `thumbs\`, `waves\`, `transcript\` (keyed by file + size + mtime; regenerable, survives rebuilds).
- **Settings:** `%LOCALAPPDATA%\FootageReviewer\settings.json`.
- **Diagnostics / crash log:** `%LOCALAPPDATA%\FootageReviewer\diagnostics.log`.
- **Backups:** `<project>.backups\` next to each `.frproj` (minute snapshots, promoted to ~10-minute checkpoints).

## Keyboard shortcuts (highlights)

`Space` play/pause (and the keyboard's **media Play/Pause key**, which works even when the app isn't focused) · `← / →` skip · `Ctrl+← / →` jump audio segments · `T` log · `E` edit last · `J` jump to recent log · `M` / `Shift+M` marker · `L` centre-lock · `Ctrl+L` zoom-lock · `D` dictate · `Ctrl+1/2/3` copy last bubble of track 1/2/3 · `Ctrl+Shift+1/2/3` log it immediately · `X` section-select (wheel resizes, `C` copies for Premiere) · `, / .` speed ±0.25× · `1–0` speed presets · `Ctrl+S` save · `Ctrl+Z / Ctrl+Shift+Z` undo/redo clip edits. The full list lives in **Settings → Keyboard shortcuts**.

## Notes

- See **CHANGELOG.md** for the per-version history. This README is updated alongside notable features.
