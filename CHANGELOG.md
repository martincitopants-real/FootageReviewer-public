# Changelog

All notable changes to **FootageReviewer**, newest first.

## 2026-09-08 — splash stays in the window (v1.18.1)

### Fixed
- **The loading splash and the Saving toast no longer float over other apps when you tab out.** Both were
  popups (their own top-level windows) - needed once to sit above the old native video window. The video is
  drawn by Avalonia now, so they're plain in-window overlays that live and die with the app window.

### Diagnostics
- The UI-stall watchdog now records every timed UI-thread op (timers, mpv property reads, transcript
  rebuild snapshot/apply, auto-level apply, audio-graph rebuild, both render passes) in a ring buffer and
  lists the ones inside the stall window with durations, instead of a single stale tag.

## 2026-09-08 — 11-second load freezes on big projects (v1.18.0)

### Fixed
- **The transcript rebuild was quadratic.** Anchoring each Whisper segment to its speech run scanned every run
  in the track; with 122k segments and tens of thousands of runs on a 150-hour project that was ~10 s on the
  UI thread, and it ran on every load, level-slider change and waveform arrival. It is a binary search now.
- **The transcript rebuild runs off the UI thread**, with a generation guard so only the newest result lands.
  The remaining per-track waveform scan (~18 M buckets) no longer blocks anything.
- **Embedded thumbnails and waveforms decode on a background task** started the moment the project file is
  read, overlapping the ffprobe/mpv load; the UI-side apply is ~2 ms. The thumbnail section could previously
  run twice per load; each section now runs exactly once.
- **Thumbnail extraction skips clips restored from the project** instead of re-reading all their cached
  frames and flashing 168 spinners.

### Diagnostics
- Load-path timings (clip layout, audio configure, asset apply, audio graph, auto-level apply, transcript
  apply) and render-pass times are logged when they exceed 100-150 ms, and UI-stall lines now include the GC
  pause and heap size, so the next slow load names its cause. Measured on a large benchmark project: the
  10.8 s block is gone; no remaining step exceeds 150 ms.

## 2026-09-08 — compositor-thread crash (v1.17.2)

### Fixed
- **Crash while scrubbing after a long session** (access violation in `combase.dll` on Avalonia's WinUI
  compositor thread, after five days running). Avalonia's WinUI-composition backend runs a COM/DirectComposition
  message loop on its own thread, and that loop is a known-fragile path. The app now uses the DXGI swap-chain
  composition mode instead: no compositor thread, same ANGLE GL surface for the mpv video, lower latency.
  Verified the GL render context still comes up on the new path.

## 2026-09-03 — crash on zoom-out (v1.17.1)

### Fixed
- **Zooming the timeline all the way out crashed the app** (hard kill, nothing in `diagnostics.log`). The
  auto-level chunk ticks were drawn with a `stackalloc` *inside* the per-chunk loop; a `stackalloc` in a loop
  takes fresh stack on every iteration and only gives it back when the method returns. Fully zoomed out on a
  150-hour project every chunk is "visible", so ~100k iterations exhausted the 1.5 MB UI-thread stack.
  Diagnosed from the crash dump in `%LOCALAPPDATA%\CrashDumps` (one 1.5 MB frame full of chunk
  start/end times). Ticks are also now de-duplicated per pixel, so that zoom level draws far less.
- **Closing or going Home while transcription was running** logged a spurious
  "No process is associated with this object" from the sidecar worker (teardown disposed the process it
  was waiting on). Treated as exited now.
- The launcher's background indexer could throw "Collection was modified" when a card finished indexing
  while it was still walking the list. It walks a snapshot now.

## 2026-08-28 — home screen, splash, and no more auto-diving into a project (v1.17.0)

### Added
- **A launcher (home screen) is now what opens first.** It lists every project you've worked on with a poster
  frame, clip count, total duration, log count, size and when you last opened it — plus **New project**,
  **Open project…** and a filter box. Clicking a project opens the workspace; closing the workspace brings the
  launcher back; **File ▸ Home** returns to it at any time.
- **The launcher deliberately doesn't build the workspace**, so it appears immediately — none of the mpv
  start-up or project loading happens until you actually pick something.
- **Loading splash** while a project opens: logo, project name and a live status line.
- **Project cards are cached** (`…\cache\projects`), keyed to each project's size and modified time, so the
  slow read only ever happens once per change. The first run indexes everything in the background, newest
  project first, with the cards filling in as it goes.

### Changed
- **Opening the last project on launch is now opt-in** (the setting still exists — there's a checkbox right on
  the launcher). Existing installs are migrated to the new default once, so you get the home screen.
- **The recent list no longer drops projects it can't currently see.** With projects on a NAS, opening one
  while the NAS was offline used to silently erase the rest of your history; unreachable projects now stay
  listed and are shown greyed with an *offline* badge. The list is no longer capped at 12.

## 2026-08-27 — relink missing footage (v1.16.0)

### Added
- **Missing-footage relink.** Opening a project whose recordings have moved no longer just shows a black
  screen: it now lists what's missing and offers to **Locate folder…** (matching your files by name, including
  subfolders), **remove the dead clips**, or open anyway. Logs, markers and sync points are keyed to time, so
  they're unaffected either way.
- When every missing clip is already in the project under a working path — the leftovers you get after moving
  a footage folder and re-importing — the dialog says so and offers removal as the one-click fix, since
  relinking those would add each recording twice.

### Notes
- Merged the macOS-port work from PRs #2 and #3 (cross-platform tool/sidecar lookup, macOS app build script);
  verified the Windows build is unaffected.

## 2026-08-15 — Premiere bridge panel, mouse-guided section tool (v1.15.0)

### Added
- **Premiere panel that actually pastes onto the timeline** (`premiere-plugin/`). Copying to the clipboard
  could never work — Premiere's timeline clipboard is a private format, and its Project panel has no
  paste-a-file handler. The new CEP panel reads what you copied and drops it onto the **open sequence at the
  playhead** (or appends to the end of V1), importing and trimming the source automatically. Optional
  auto-paste places each section the moment you press **C**. One-time install via `Install plugin.ps1`; the
  generated `.xml` still works as a no-install fallback via *File ▸ Import*.
- **Logs within ±5s of the playhead** are highlighted in a distinct violet.

### Changed
- **The section tool now follows the mouse**, not the playhead: aim with the cursor, **wheel** resizes,
  **Alt+wheel still zooms**, and the selection runs **forward** from the cursor by default — hold **Shift**
  to extend it both ways.

### Fixed
- **Committing a log edit no longer hangs** for about a second (it re-groups the single row instead of
  rebuilding the whole non-virtualised log list — the same fix already applied to delete and marker drag).

## 2026-08-14 — section copy for Premiere, media key, keep-playhead-visible (v1.14.0)

### Added
- **Copy a section of footage for Premiere.** Press **X** to open the section tool (a band centred on the
  playhead), **mouse wheel** over the timeline to resize it, **C** to copy, **Esc** to close. The copy writes
  an FCP7 XML (`xmeml`) sequence for exactly that span — spanning multiple recordings if the selection
  crosses clip boundaries — and puts that file on the clipboard, so pasting into Premiere's **Project panel**
  imports a sequence that relinks your original footage at the right in/out points.
  *Note:* Premiere's own timeline clipboard is a private format no third-party app can write, so this is the
  supported interchange route rather than a literal paste onto the timeline.
- **Keep playhead visible** (👁 next to 🎯): the view stays where you put it, but jumps back to the playhead
  whenever it scrolls out of sight — looser than centre-lock.
- **Media Play/Pause key** now toggles playback **even when the app isn't focused**, so you can pause while
  tabbed out.
- **Ctrl+Shift+1/2/3** logs the last transcript bubble for that track **immediately**, instead of parking it
  in the log box (any draft you were typing is preserved).

### Fixed
- **Clicking a log now moves the timeline view to it**, regardless of the centre-lock state.
- **Deleting a log is no longer laggy** — it removes the single row instead of rebuilding the entire
  (non-virtualised) log list.

## 2026-08-04 — auto-level stutter fix (v1.13.2)

### Fixed
- **The recurring ~800 ms freezes are gone.** Rebuilding the auto-level map was the app's worst stall, and it
  re-ran every time a waveform finished generating (so it hit repeatedly while a project loaded):
  - it scanned the whole waveform to find speech runs, then ran a **separate peak scan per run that looped
    over every clip** — the peak is now measured in the same single pass (removes an O(runs × clips) blowup);
  - the whole computation now runs **off the UI thread** and posts the finished map back, so playback and
    scrolling stay smooth while it works;
  - the waveform was being scanned twice per track per update (once for the transcript display, once for
    auto-level) — auto-level's pass no longer happens on the UI thread at all.
- Re-added the **UI-stall watchdog**: any hitch over 400 ms is written to `diagnostics.log` with the name of
  the operation responsible, so future stutters can be traced.

## 2026-08-04 — search crash fix (v1.13.1)

### Fixed
- **Searching no longer crashes the app.** Several failure paths in both search boxes could throw straight out
  of an event handler or the render pass, which takes the whole program down:
  - a transcript segment with no text (possible in a project restored from file) crashed the **render pass**
    when the search compared against it — now null-safe;
  - jumping to a match before the log list had finished laying out could set a **non-finite scroll offset**,
    which throws inside Avalonia's layout — now ignored;
  - the log list was iterated while search highlighting fired change notifications — now snapshotted.
- **Searching is much faster on big projects.** Both searches scan everything (every log entry / every
  transcript segment on every track) and then repaint the non-virtualised log list — that ran on *every
  keystroke*. It's now debounced to when you stop typing.
- Any remaining search fault is caught, written to `diagnostics.log`, and shown as a status message instead of
  killing the app.

## 2026-06-18 — faster waveforms, loading spinners, transcript Stop, copy-chaining, fixes (v1.13.0)

### Fixed
- **Logs now follow a moved clip** reliably — the clip-edit step that remaps log times no longer gets skipped
  if the (cosmetic) asset-carry hits a snag; the remap always runs, and it's recorded in the diagnostics log.
- **Transcription progress after a restart** reflects only the clips that still need work — already-cached
  clips are applied for free instead of being re-counted (no more "3/171" when most are done).
- **Hovering a transcript bubble** highlights it (and its copy button) based on the bubble's *visible* size, so
  short utterances drawn taller than their time-span respond to the mouse.

### Added / changed
- **Waveform generation runs in parallel** (several decodes at once instead of one-at-a-time) — much faster on
  a big project, with no change to waveform detail.
- **Loading spinners** appear on a clip's video lane while its filmstrip generates, and on each audio segment
  while its waveform generates.
- **Stop transcription** — the Generate button now greys out while running and a **⏸ / ⏹** pair lights up next
  to it: pause toggles to a ▶ play glyph, stop cancels and resets (keeping what's already transcribed).
- **Copy-chaining**: clicking a second bubble's copy button from the **same** track merges it into the previous
  quote (`Me: "a"` + `"b"` → `Me: "a b"`); a different track starts a fresh prefixed quote as before.
- **Autocorrect** gained multi-word phrase fixes (e.g. "int he" → "in the", "would of" → "would have") plus
  ~30 more single-word corrections.
- Added a **README** at the repo root.

## 2026-06-18 — clip-edit crash fix + logging, marker-drag fix, log offset, redo, rename (v1.12.0)

### Fixed
- **Moving a clip could crash** — added a crash log (`%LOCALAPPDATA%\FootageReviewer\diagnostics.log`) that
  captures unhandled exceptions, and the clip-rebuild step now recovers from a fault instead of crashing (or
  permanently wedging further edits).
- **Moving a clip made the timeline briefly shrink** (audio lanes collapsed then re-expanded) — fixed; the
  timeline height stays put through a reorder.
- **Dragging a log marker** (either timeline) caused a big lag spike and yanked the playhead to the drop point —
  both fixed: it now retimes the log instantly and leaves the playhead alone.
- **Drag-and-drop import** of video files onto the timeline now works (the drop is accepted by the timeline's
  container).
- **Zooming far out in the transcript** no longer lags: it skips straight to the visible bubbles and culls
  sub-pixel slivers (active / search-match / Ctrl-copy-target bubbles are always kept).
- **Regenerate waveforms / thumbnails** now shows only the type being rebuilt in the progress label.
- **Off-screen Ctrl-highlight bubbles** now show the **whole** log (no more truncation, no arrow).

### Added
- **New-log offset** (Logs ⋮ menu) — shift each new manual log's time by ±N seconds (e.g. −1 so it lands a
  second before you logged it). Not applied to text copied from the transcript.
- **Hide buttons + search** toggle (Logs ⋮ menu) — collapse the header buttons, search bar and clip label for
  more log-text room (the ⋮ menu stays).
- **Click a log snaps the timeline view** option (Logs ⋮ menu) — clicking a log centres both timelines on it,
  even when the playhead view is locked.
- **Ctrl+Shift+Z redo** for all clip operations (reorder / insert / delete / chronological order / undo).
- **File → Rename project…** — renames the `.frproj` on disk (and its backups folder).

## 2026-06-18 — seamless clip edits, undo, chronological order, draggable transcript markers (v1.11.0)

### Added
- **Drag transcript markers** to retime their log entry — same as the video timeline (a plain click on the
  marker tag jumps there).
- **Timeline ⋮ menu** (next to the lock-playhead button) → **Order footage chronologically**. A confirm dialog
  lets you sort by **recording time in the filename** (OBS `YYYY-MM-DD HH-MM-SS`, the default), **file modified
  date**, or **file created date**. Logs, sync points and the playhead follow their clip.
- **Ctrl+Z undoes clip operations** — reorder, drag-drop insert, delete, and chronological order (restoring the
  order, logs, sync points and playhead; even logs a delete removed). Doesn't touch text editing in the log box.
- **File → Open recent** — a hover submenu of recent projects (de-duped, missing files dropped), plus *Clear recent*.
- **Settings → Cache & assets** → **Regenerate thumbnails** / **Regenerate waveforms** (separately): deletes the
  cached assets for the loaded footage and rebuilds them from the video.
- When holding **Ctrl**, a copy-target transcript bubble that's scrolled **off-screen** is now pinned as a
  readable copy at the top of its track column (with an arrow showing where the real bubble is).

### Fixed
- **Reordering / inserting / deleting clips is now seamless** — the timeline no longer blanks out for half a
  second, and it no longer re-probes every clip (only genuinely-new files are probed). Thumbnails, waveforms and
  audio levels carry over to the clip's new position instead of flickering.
- **Moving clips no longer resets the view** — the playhead, play/pause state, speed and zoom/scroll are all
  preserved (it used to jump to the start, start playing, and zoom all the way out).
- **Drag-and-drop import now works when the project already has clips** (previously it only worked on an empty
  project).

## 2026-06-18 — import logs from text (v1.10.0)

Paste a block of timestamped lines straight into your logs.

### Added
- **⋮ menu → "Import logs from text…"** in the Logs tab. Paste a block; each timecoded line becomes a log
  entry, **added** to your current logs (no merge).
- Handles both formats: the app's own **"Copy all"** output (`H:MM:SS⇥text`) and hand-written logs
  (`8:00 meet the dealer`). Recorder/date header lines and wrapped continuation lines are handled
  automatically.
- When a block contains **two-part** timecodes (e.g. `8:00`, which is ambiguous), the dialog asks whether to
  read them as **hours:minutes** or **minutes:seconds** before importing.
- Pasted timecodes are treated as **display** values and mapped back through your timer-sync offsets, so
  re-imported "Copy all" output (and timer-based logs) land on the right frame.

### Fixed (from adversarial review of this feature)
- **Back-to-back recording blocks no longer corrupt a log.** A date header between entries (when several
  blocks are pasted with no blank line between) is now skipped instead of being appended to the previous
  entry's text.
- **Logs imported before any footage now persist.** A logs-only project (path set, no clips yet) is saved
  immediately after import and is covered by autosave + rotating backup — previously it lived only in memory
  until a manual Ctrl+S.
- **Importing is serialized against an in-flight clip insert/reorder**, so a deferred clip remap can't
  double-remap or mistime freshly imported entries.
- **The display→video-time inverse is robust to non-monotonic sync points** (a timer reset/typo no longer
  clamps an imported entry to 0).

## 2026-06-18 — editable timeline: drag-drop import + reorder clips (v1.9.0)

M18 Phases 2 & 3 — the footage sequence is now editable, and your notes follow their clip.

### Added
- **Drag video files from Explorer onto the timeline** to import them. They're inserted at the **nearest
  clip boundary** to where you drop (or first-loaded into a fresh project, prompting Save As if needed).
- **Reorder clips**: right-click a clip to select it (the selection now highlights its **video + all audio
  lanes**), then **left-drag** it to a new boundary (a green indicator shows where it'll land).
- **Logs, sync points, and the playhead follow their clip** when you insert or reorder — their times are
  remapped by clip + in-clip offset, so a note stays on the moment it describes. The transcript re-derives
  at the new positions from its per-clip cache.

### Notes
- A plain click on a selected clip still seeks (a small drag threshold distinguishes click from drag).
- Clip edits are serialized (one reload at a time) and persisted by autosave / on close (so a reorder never
  writes a half-rebuilt project with blank transcript/thumbnails).

## 2026-06-18 — log count, transcript marker hover, project-save-first (v1.8.0)

First of three M18 phases (drag-drop + reorder follow). This phase:

### Added
- The **Logs** header shows a live count (**"Logs · N"**).
- **Transcript markers** now have a colored tag on the left edge and show the **log text on hover**
  (mirrors the video timeline's markers).
- ~70 more common typos in autocorrect (e.g. `abuot`, `recieve`-family, `tehy`, `verison`, `wonderfull`).

### Changed
- **A project now always has a save location.** Starting a **New Project** prompts **Save As** first
  (cancel keeps your current project). Importing footage with no project open prompts you to save the
  project first, and footage can't be imported without an active, saved project. Emptying all clips keeps
  the project (and its file), rather than demoting it to an unsaved state.

## 2026-06-18 — Ctrl-copy highlight, fine speed, smoother waveforms, mp4 handling (v1.7.0)

### Added
- **Hold Ctrl** to highlight (green outline) the transcript bubbles that **Ctrl+1 / 2 / 3** would copy.
- **L** — lock the timeline view to the playhead (centre-lock).
- **J** — jump to the most recent log.
- **Shift+M** — remove the temporary marker.
- **`,` / `.`** — slow down / speed up by 0.25× (Shift = 1× steps); playback below 1× is now allowed and
  the chosen speed is saved with the project.
- **CAPS: ON** indicator above the log box while Caps Lock is on.
- Autocorrect: `hte → the`.

### Changed
- The dictation mic dropdown now shows **"Windows Default (<your real default mic>)"**, and "Windows
  Default" records from the actual Windows default capture device (not just the first one ffmpeg lists).
- The new hotkeys are listed in **Settings → Keyboard shortcuts** and the bottom legend (one shared source).

### Fixed
- **Timeline waveforms were still jumpy under centre-lock** — the envelope's vertices were pinned to whole
  pixels, so it morphed in place instead of translating. It now slides sub-pixel smoothly.
- **The "lock log playhead" only worked while playing** — it was gated on playback. It now re-centres
  whenever the playhead moves (play **or** seek while paused), while leaving a paused manual scroll alone.
- **.mp4 thumbnails / waveforms** — a video-only / fewer-track mp4 no longer hard-errors the waveform
  extraction (optional stream map), and a file ffmpeg can't read now shows a clear **"mp4 not finalized?
  use the .mkv"** warning instead of silently producing blank thumbnails. (A *finalized* mp4 works fully;
  see the note below.)

> **mkv vs mp4:** OBS-to-**.mkv** is crash-safe — Matroska is readable even if a recording is interrupted.
> **.mp4** only becomes readable once it's *finalized* (its "moov" index is written on a clean stop), so a
> still-recording / crashed / interrupted mp4 can't be probed and yields no thumbnails or waveforms. Keep
> **.mkv** as the capture format; use **.mp4** for finalized exports.

## 2026-06-18 — smooth playhead, instant log insert, compact bubbles (v1.6.0)

### Added
- **All hotkeys now listed in Settings → Keyboard shortcuts**, built from one shared list that also feeds
  the bottom legend, so the two can't drift (and it now includes E / D / the copy-bubble keys).

### Changed
- **Copy-last-bubble hotkeys moved from V / B / N to Ctrl+1 / Ctrl+2 / Ctrl+3** (track 1 / 2 / 3; NumPad
  works too). They fire from the log box or anywhere outside a text field, and free up V/B/N for typing.
- **Single-line transcript bubbles are smaller** — a bubble now fits its text (one line + padding) instead
  of the old fixed 36 px floor; multi-line bubbles still grow to fill their duration.
- The **"Dictation: nothing transcribed"** (and other transient dictation messages) now auto-clear after 5s.

### Fixed
- **Playhead + timeline are smooth under centre-lock** — a 60 Hz interpolator now predicts the playhead
  between mpv's ~8 Hz polls and drives both timelines, with the playhead drawn sub-pixel/antialiased. This
  is the real fix for the audio waveforms "jumping" and the jumpy centre-lock scroll. It re-anchors on
  seek and speed change, eases back gently on prediction overshoot, and stands down during launch restore.
- **New logs appear instantly** — a freshly committed log is inserted into its existing folder instead of
  clearing and rebuilding every clip folder (the ~1s delay); the per-log save stays immediate (crash-safe).

## 2026-06-18 — hotkey legend, mic fix, faster log edits, waveform AA (v1.5.0)

### Added
- **Hotkey legend** along the bottom bar (next to the % complete); full list in its tooltip.
- Autocorrect now fixes a lone **"i" → "I"**.
- The colour menu's first entry now reads **"Amber (default)"**.

### Fixed
- **Dictation found no microphones** — the device parser didn't understand the bundled ffmpeg's newer
  `(audio)`/`(video)` output format (and could mis-list webcams from the classic format); both are handled now.
- **Lag spike when adding / editing / deleting a log** — the project save was re-encoding *all*
  thumbnails + waveforms to base64 every time; that's now cached and only rebuilt when the media changes.
- **Waveforms dance / no antialiasing** under centre-lock — the lanes are now drawn antialiased (a nested
  render-option override wasn't taking effect), so the envelope scrolls smoothly; lane separators stay crisp.
- **Mic button** no longer overlaps the log text/watermark and its icon is centred (the box reserves room).

### Changed
- The **no-microphone** message can be dismissed (✕).
- (Global Settings already mirrors the Logs/Transcript ⋮ options; the timer-sync stays in the ⋮ as it's
  per-project data, not an app setting.)

## 2026-06-18 — dictation, auto-capitalize, merge/split logs, search nav (v1.4.0)

### Added
- **Dictation** — a 🎤 button in the log box and hotkey **D** record your mic (pausing if auto-pause is
  on), then on the second press resume playback immediately and transcribe in the **background**; the text
  is added as a log stamped at the moment you started. Pick the mic in **Settings → Dictation**.
- **Auto-capitalize** the first word of each sentence (toggle in Settings). Backspace now cascades:
  1st undoes the capitalization, 2nd undoes the autocorrect, 3rd deletes normally.
- **Merge logs**: each log has a **⇅** menu (merge up / down) and auto-joined logs get a **⎘ split**
  button to break them back apart (the original parts are remembered + saved).
- **Right-click a log** to colour it (same palette as markers).
- **Search**: prev/next-match buttons **inside** the Logs search box, and an **✕ clear** on both search
  boxes; the count shows `i/N`.
- **Version number** in the toolbar + title.

### Fixed
- **Waveforms no longer dance** under centre-lock — the peak mapping follows the true sub-pixel scroll
  (no more freeze-then-jump) and the waveform is now antialiased.
- **Volume %** readout now restores correctly on load (was stuck at 100%).
- **Right-click spelling suggestions** now reliably appear on a squiggled word (resolved on-demand under
  the cursor, runs before the default menu).
- Review fixes: dictation re-entrancy guard + off-thread mic enumeration (no UI freeze) + temp-file
  cleanup + resume-only-if-was-playing; merge preserves the copied flag; editing a merged log no longer
  reverts on split; auto-merge scroll targets the right row; log search refreshes after edits.

### Changed
- **On-screen timer sync** moved into the Logs **⋮** menu.
- **Transcript bubbles** no longer show a timestamp inside the bubble (more room for text; the time ruler
  stays on the right).

## 2026-06-17 — resizable/hideable transcript tracks, marker drag, more fixes

### Added
- **Drag the transcript track columns** to re-scale them (grab the divider in the lane body), and a new
  **⊘ hide-track** button (next to the 👁 hide-bubbles button) that removes a track entirely — the
  remaining tracks expand to fill. Column widths + hidden tracks are saved per project.
- **Drag a timeline marker** to retime it (its log entry moves with it); a plain click still jumps to it.
- Hotkeys **V / B / N** copy the most-recent bubble of **Track 1 / 2 / 3** into the log (same as the
  bubble copy button). M still drops a temporary marker.

### Fixed
- **Auto-zoom on launch (root-caused for real):** projects saved with **centre-lock on** never ran the
  centre-on-restore (it was gated to the non-locked path), and a centre-locked timeline followed the live
  playhead — still at 0 until a deep (50 h) NAS seek lands — so the view sat at the start. The restore now
  centres on the saved time unconditionally, and the restore-seek is throttled so a slow NAS seek can land.
- **Waveforms no longer "dance"** when playing zoomed-out — the peak-per-pixel mapping is anchored to a
  whole-pixel grid (was drifting with sub-pixel scroll).
- **Generate** now reports the correct done count — it scans the cache for each clip's actually-transcribed
  tracks instead of failing on a phantom extra audio track (which made it say "0 of 57").
- The **preview ⇆ tabs splitter** is draggable again (the auto-sizer was snapping the video column back and
  the drag fell through to the log scroll); the grab strip is also a touch wider.
- Divider-drag crash guard (very narrow column pairs), marker-drag no longer drifts under center-lock, and
  V/B/N no longer swallow Ctrl/Shift/Alt chords.

### Changed
- **Manual Log → Logs**, **Auto Transcript → Transcript**.
- Per-track **Gap length sliders moved into the ⋮ menu**; the track strip is now a compact row of toggles.

## 2026-06-17 — spell-check & autocorrect, recurring-bug fixes

### Added
- **Autocorrect** in the log box: a curated list of common typos is fixed as you type (on space /
  punctuation), with **one-backspace undo** (phone-keyboard style) and casing preserved. On by default
  (Settings → Manual Log). Deliberately exact-match only, so slang / game names / usernames are safe.
- **Spell check** in the log box: misspelled words get a **red squiggle**; **right-click** one for
  suggestions or **Add to dictionary** (your additions persist). On by default; both toggle in Settings.
  Uses an embedded en_US dictionary (Hunspell); the check runs off the UI thread.
- **Clip number** in each Manual Log folder name, e.g. `2026-05-21 12-47-17 (25/57)`.

### Fixed
- **Auto-zoom on launch (for real this time)**: root-caused to the timeline's duration being wiped to 0
  by the per-clip probe, which made the centre-on-playhead a no-op and pinned the view to the start. The
  timeline now keeps the mpv duration and re-asserts it before centring.
- **Slider thumbs are now actually smaller** — the Fluent template sizes the thumb via theme resource
  keys (not a style setter), so previous attempts had no effect; the right keys are now overridden.
- **Gap-length numbers** are no longer clipped by the divider line beneath the track strip.

### Changed
- **Lock log playhead** no longer freezes the scrollbar: you can scroll freely while paused; it only
  snaps back to centre during playback.
- The log playhead line is now the **same red** as the video/transcript playheads (was cyan).

## 2026-06-17 — manual-log playhead, sticky folder & launch polish

### Added
- **Manual Log playhead** — a thin horizontal line that snaps discretely to the log (or clip header)
  nearest the current playback time; once you're past the last log it rests at the bottom of the list.
- **Lock the log playhead to centre** (🎯 in the Manual Log header) — the list auto-scrolls so the
  current point stays centred, using the *same* centre as when a new log is added, so logging never
  jumps the view. Saved per project.
- **Volume faders are saved** with the project.
- Hotkeys: **T** pauses immediately (no longer waits for you to type); **E** pauses and edits the most
  recent log (when auto-pause is on).

### Fixed
- **Auto-zoom on launch** now lands on the **saved playhead** — it re-seeks until the time settles
  (bounded so it can never loop), instead of snapping to the start of the footage.
- **Current-folder label** is now a true sticky header: it shows the clip whose header has scrolled
  **off the top** of the view (driven by scroll position, not the playhead).
- Lock-to-centre auto-scroll no longer risks a scroll feedback loop (added a re-entrancy guard); the
  playhead line recomputes its geometry every frame so it stays correct after a row changes height
  (e.g. entering edit mode).

### Changed
- **Sync points** is now a compact dropdown **next to the search box** (same height); pressing Space
  after clicking it no longer re-opens it.
- The app **starts maximised on the primary monitor**.
- **Generate** confirmation now reads "Transcribe *N* of *M* clips (*X* already done)".
- **Gap length**: label sits beside the 👁 button, the number is pulled up, and the whole strip is
  shorter; slider **thumbs** are smaller (template override so the change actually takes).
- The **preview ⇆ tabs** divider is wider, so you can resize the tabs against the video.

## 2026-06-17 — log merge, audio order, layout & transcript fixes

### Fixed
- **Auto-level vs volume**: the auto-leveller (`dynaudnorm`) now runs *before* the volume fader, so the
  fader scales the normalized signal — consistent loudness across the clip instead of the two fighting.
- **Transcript bubbles**: text is now anchored to the run nearest each segment's **start** (distance-
  capped), so a Whisper segment with a bad end timestamp can't dump its text onto a far-away block.
- **Auto-zoom on launch** now centers on the **playhead** (where you left off) instead of the start.
- **Resizable tabs** actually work now (the old divider was sitting on top of the splitter and eating
  the drags).
- Centered the audio-track **reset/⇥** icons; the Auto Transcript **⋮** is square; tab-header buttons
  are a consistent size/shape; transcript **Track** headings are bolder (match the timeline).

### Added
- **Merge nearby log entries**: a new log within N seconds (configurable in Settings, default 5) of an
  existing one merges into it under the earlier timestamp.
- **Current clip** is always shown at the top of the Manual Log (under the search box).
- **Generate** now asks for confirmation, showing how many clips × tracks it will transcribe.

### Changed
- Manual-log typing box moved to the **bottom** of the tab and made taller.
- On-screen-timer-sync info collapsed into a hover **ⓘ**; sync-points list compacted.
- Slider thumbs **halved** again; audio in/out marks recoloured **light green**.

## 2026-06-17 — chrome, layout & save-state polish

### Fixed
- **"Unsaved changes" indicator** now reflects only real edits (logs, markers, transcript…); playhead
  and zoom are still saved silently but no longer flag the project as unsaved.
- **Auto-zoom on launch** now holds — re-asserts the saved zoom until you actually zoom/pan, and no
  longer "loads then zooms back out" (including for centre-locked projects).
- **Bubble font-size number** in the ⋮ menu no longer cut off; **"Gap length" label restored** and the
  track-control strip resized so the "Transcript ready" line no longer clips into the readouts.
- Centered the icons on the audio-track **reset (⟳)** and **segment-skip (⇥)** buttons.

### Added
- **Resizable split** between the Manual Log and Auto Transcript tabs (drag the divider).
- Native **dark window title bar** (app set to the Dark theme).

### Changed
- **Settings** moved into the menu bar next to **File** (sleek menu style).
- Removed the **±5s** transport buttons (arrow keys still skip); the guide-line toggle got a new icon.
- Audio in/out **chunk marks** recoloured light grey so they don't clash with marker colours.

## 2026-06-17 — audio + UI refinements

### Fixed
- **Auto-level no longer skips the audio** at the end of each speech segment — replaced the per-segment
  filter-graph rebuilds with a single `dynaudnorm` dynamic normalizer (set once, no runtime swaps).
- **3-dot / ⋮ flyout lag** on open/close — per-track gap & silence-level edits now only trigger a
  transcript rebuild when the value actually changed.
- **Auto-zoom restore on launch** re-asserts the saved zoom for several ticks so launch layout timing
  can't drop it.

### Added
- **Vertical time ruler** down the right edge of the Auto Transcript timeline.
- **Backspace on an empty log box** resumes playback and unfocuses it.
- The transcript **⋮ menu mirrors + syncs** the global Auto Transcript settings; per-track segment-skip
  (**⇥**) inclusion now persists between sessions.

### Changed
- **Settings** moved into the menu bar next to **File** (sleek menu style); removed the standalone button.
- Auto-level **Max** lives in Settings (was the toolbar slider).
- Copying a transcript bubble now **appends** to the log box instead of replacing it.
- Track controls: **eye toggle centered**, gap sliders take ~half the vertical space, readout **+2pt**,
  **smaller slider thumbs**; soloing a muted track **unmutes** it.

### Removed
- The stutter **diagnostics log** feature (cause identified — see auto-level fix above).

## 2026-06-17

### Added
- **CHANGELOG** + auto-push: changes are now committed and pushed to GitHub automatically, with an entry here.
- **Scale skip distance by playback speed** (Settings) — at 2× the ←/→ skip is 2× as far; guide lines + ⏪/⏩ labels reflect it.
- **Marker colour** now tints the timeline line *and* the matching manual-log row (default amber stays un-tinted).
- **"Generating…"** state — the Generate button greys out while a transcript is being made.
- **Stutter diagnostics** — a UI-stall watchdog writes to `%LOCALAPPDATA%\FootageReviewer\diagnostics.log`.
- **Live preview** in Settings — skip distance and transcript font size update as you drag, before Save.

### Changed
- Top buttons (Open footage … Save as) collapsed into a Premiere-style **File** menu.
- App-wide **slider theming**; per-track **M/S/A** toggles are colour-coded (green/yellow/blue), bold, centered.
- Track names are now **"Track 1/2/3"** (dropped the `1.`/`2.`/`3.` prefix).
- **Gap-length** sliders are horizontal.
- **Auto-level Max** and the per-tab options are mirrored into **Settings** (synced both ways).
- Auto-transcript options moved to a **⋮** menu beside **Generate**; headers capitalised ("Manual Log", "Auto Transcript").

### Removed
- Untracked the icon source PNGs from the repo (`icon.png`, `icon 2.png`, `icon 3.png`, `Assets/icon.png`) — the app ships `Assets/icon.ico`; local copies are kept and gitignored.

### Earlier this day (initial commit)
Full first version: embedded libmpv playback, multi-track audio mixing with mute/solo/volume,
manual log with clip folders + search + copy-all (strikethrough), GPU auto-transcript
(faster-whisper distil-large-v3) on a vertical per-track timeline, dual zoomable timelines,
per-chunk auto-level ("mumble booster"), on-screen-timer re-sync points, temporary markers,
Ctrl/Shift speed + skip hotkeys, transcript-bubble → manual-log with speaker prefixes,
versioned rotating project backups, and a single-file Windows build.
