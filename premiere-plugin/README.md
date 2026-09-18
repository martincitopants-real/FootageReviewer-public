# FootageReviewer → Premiere bridge

Puts a section of reviewed footage onto your Premiere timeline at the playhead.

## Why a plugin at all?

Premiere's timeline clipboard is a **private, undocumented format**. No outside app can put something on the
Windows clipboard that Premiere will accept as a timeline paste — and its Project panel has no "paste a file"
handler either, so `Ctrl+V` there does nothing. The supported routes are:

| Route | What it does | Needs |
|---|---|---|
| **This panel** | Drops the section straight onto the open sequence at the playhead | one-time install |
| `File ▸ Import` the generated `.xml` | Imports a sequence containing the section, which you drag in | nothing |

The panel is **CEP**, not UXP, on purpose: as of Premiere 2025 the UXP API still can't add clips to a
sequence, while CEP/ExtendScript has had `Track.overwriteClip()` for years.

## Install

Close Premiere, then run **`Install plugin.ps1`** (right-click ▸ Run with PowerShell, or from a terminal).
It does two per-user things — no admin, nothing system-wide:

1. copies the panel to `%APPDATA%\Adobe\CEP\extensions\com.footagereviewer.paste`
2. sets `PlayerDebugMode=1` under `HKCU\Software\Adobe\CSXS.*`, which Adobe requires to load an extension
   that isn't signed with a paid Adobe certificate

Then start Premiere → **Window ▸ Extensions ▸ FootageReviewer Paste**.

## Use

1. In FootageReviewer press **X**, aim with the mouse, **wheel** to size the selection (**Shift** = extend both
   ways, **Alt+wheel** still zooms), then **C** to copy.
2. In Premiere, click **Paste section at playhead** (or **Append to end of V1**).

Tick **Auto-paste** to have the panel place each section the moment you press C in FootageReviewer.

## How it works

Pressing C writes `%LOCALAPPDATA%\FootageReviewer\cache\premiere\handoff.json` (source paths + in/out
seconds) alongside an FCP7 XML of the same range. The panel reads that file, imports the recording if it
isn't already in your project, trims it to the range, and calls `overwriteClip()` on V1 at the playhead.
Sections spanning several recordings are laid down back-to-back.

## Troubleshooting

- **Panel missing from the Extensions menu** — the install script didn't run, or Premiere was open during
  install. Re-run it with Premiere closed.
- **"Open a sequence in Premiere first"** — the panel places clips into the *active* sequence; open one.
- **"Could not place the clip"** — usually the footage path isn't reachable (e.g. the NAS is offline).
  The paths come from your project, so they must resolve from this machine.
