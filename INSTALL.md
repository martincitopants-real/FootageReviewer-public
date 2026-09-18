# Install FootageReviewer

This guide takes you from downloading the files to opening your first video. You do not need to know how to code or install Git or Visual Studio.

**There is currently no ready-made installer in this repository.** The Windows instructions below use the included build script, which turns the downloaded source files into an app for you. You will download a few supporting files first. An internet connection is needed during setup.

Start with [Windows setup](#windows-setup). [Automatic transcription](#optional-automatic-transcription-on-windows) is an optional extra; you can watch footage and write notes without it. [Mac setup](#mac-setup) is covered separately and uses Terminal.

## Windows setup

These steps are for Windows 10 or 11 on a 64-bit Intel or AMD PC. They do not cover Windows on ARM. To check your PC, open **Settings → System → About** and look for an **x64-based processor** under **System type**.

### 1. Download FootageReviewer

1. Open the [FootageReviewer repository](https://github.com/martincitopants-real/FootageReviewer-public).
2. Above the file list, click the green **Code** button, then **Download ZIP**. If the repository is still private, you must be signed in with an account that has access.
3. In your Downloads folder, right-click the ZIP and choose **Extract All…**.
4. Move the extracted folder somewhere you want to keep it, such as **Documents**. You can rename it **FootageReviewer**.
5. Open that folder. You should see `README.md`, `native`, `src`, and `Rebuild dist exe.cmd`. If you only see another folder, open that inner folder first.

In the rest of this guide, **your FootageReviewer folder** means the folder containing those files. Work in the extracted folder, not inside the ZIP.

### 2. Install Microsoft's .NET tools

1. Open Microsoft's [.NET 8 download page](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).
2. Under **Build apps – SDK**, choose the **Windows x64 installer** for the latest **8.0** SDK.
3. Run the installer and finish the installation. The **SDK** is needed because the next steps build the app; installing only the Runtime is not enough.

The SDK also includes the software needed to run the app. You do not need Visual Studio.

### 3. Add the video player file

FootageReviewer needs a file called `libmpv-2.dll` to play video.

1. Open the [Windows mpv downloads](https://github.com/shinchiro/mpv-winbuild-cmake/releases). These builds are linked from the [mpv project's installation page](https://mpv.io/installation/).
2. Under the newest release's **Assets**, choose the `.7z` file whose name begins **`mpv-dev-x86_64-`**, followed immediately by a date. Choose the regular version without `v3`. The `dev` download contains the DLL; the ordinary player download is a different package.
3. Extract the download. If Windows cannot open `.7z` files, install [7-Zip](https://www.7-zip.org/) using its **64-bit x64** installer, then right-click the download and choose **7-Zip → Extract to…**. On Windows 11, you may need **Show more options** first.
4. Find `libmpv-2.dll` in the extracted files.
5. In your FootageReviewer folder, open `native` and create a folder named **`win-x64`**.
6. Copy `libmpv-2.dll` into that new folder.

You only need to copy the file; you do not need to open the DLL or install the separate mpv player.

### 4. Add the thumbnail and audio tools

1. Open the [FFmpeg Windows builds page](https://www.gyan.dev/ffmpeg/builds/).
2. In **release builds**, download **`ffmpeg-release-essentials.zip`**.
3. Right-click the downloaded ZIP and choose **Extract All…**.
4. Open its extracted folder, then open **`bin`**.
5. Copy **`ffmpeg.exe`** and **`ffprobe.exe`** into the same `native → win-x64` folder you made in step 3.

You should now have this layout:

```text
FootageReviewer/
├── Rebuild dist exe.cmd
├── Launch FootageReviewer.cmd
├── native/
│   ├── whisper/
│   └── win-x64/
│       ├── libmpv-2.dll
│       ├── ffmpeg.exe
│       └── ffprobe.exe
└── src/
```

Copy the three files themselves into `win-x64`, rather than putting their entire download folders there.

### 5. Build the app

1. Return to your FootageReviewer folder.
2. Double-click **`Rebuild dist exe.cmd`**. A text window will open.
3. Wait while it downloads the app's remaining components and builds. Keep the window open until it finishes.
4. Check that it did not report a build error and that a new **`dist`** folder contains **`FootageReviewer.exe`**, `libmpv-2.dll`, `ffmpeg.exe`, and `ffprobe.exe`.
5. Press a key when prompted to close the text window.

The script prints a final “Done” line even if something failed earlier, so the files in step 4 are the useful check. If the executable is missing, use [Troubleshooting](#troubleshooting).

### 6. Start using FootageReviewer

1. Double-click **`Launch FootageReviewer.cmd`** in your FootageReviewer folder.
2. On the home screen, choose **New project**.
3. Choose **File → Open folder…** and select a folder containing your videos. Allow time for thumbnails and audio waveforms to appear.
4. Press **Ctrl+S** and choose where to save your project. A `.frproj` file holds your notes and review progress; keep your original video files too.
5. Press **Space** to play or pause. Press **T**, type a note, and press **Enter** to save the note at that point in the footage.

For future sessions, use **Launch FootageReviewer.cmd** again. You only need the rebuild script after changing or updating the app files. Keep the whole FootageReviewer folder together: moving only the executable will leave supporting files behind.

## Optional: automatic transcription on Windows

Skip this section if you only want video playback and typed notes. Transcription and microphone dictation need an additional Python setup. The instructions here use your computer's main processor, so an NVIDIA graphics card is not required. Transcribing long recordings this way can be slow.

### Install Python

1. Open the [official Python Windows downloads](https://www.python.org/downloads/windows/).
2. Find the latest **Python 3.13** release and download its **Windows installer (64-bit)**. Choose the regular installer, not the embeddable package.
3. Run the installer, select **Add python.exe to PATH**, leave the Python launcher enabled, and complete the installation.
4. Close FootageReviewer before continuing.

### Set up the speech recognizer

1. Open your FootageReviewer folder in File Explorer.
2. Click the address bar at the top, type **`cmd`**, and press **Enter**. This opens Command Prompt in the correct folder.
3. Paste each command below and press **Enter**, waiting for it to finish before running the next. If a command reports an error, stop and check the troubleshooting table.

First, create a separate Python installation for this app:

```bat
py -3.13 -m venv native\whisper\.venv
```

Then download the speech-recognition software:

```bat
native\whisper\.venv\Scripts\python.exe -m pip install faster-whisper
```

Finally, paste this whole line. It creates the app's settings file using the correct Python location automatically. It replaces `native/whisper/engine.json` if that file already exists, so skip it if you already have a working custom transcription setup.

```bat
native\whisper\.venv\Scripts\python.exe -c "import json, pathlib, sys; pathlib.Path('native/whisper/engine.json').write_text(json.dumps({'python': sys.executable, 'script': 'transcribe.py', 'model': 'small', 'device': 'cpu', 'compute': 'int8'}, indent=2), encoding='utf-8')"
```

Open FootageReviewer again, load a video, and click **Generate** in the transcript panel. The first run downloads a speech model and may take a while before text appears. Speech recognition runs on your computer. You do not need an API key or a paid transcription account.

These steps use the CPU mode supported by [faster-whisper](https://github.com/SYSTRAN/faster-whisper). The app may still display a “GPU” progress label; this configuration uses the CPU. NVIDIA acceleration requires additional setup described in that project's GPU requirements.

## Mac setup

The Mac version currently needs Terminal commands. This route uses [Homebrew](https://brew.sh/), whose current macOS requirement is **macOS 14 Sonoma or newer**. There is no ready-made Mac installer here.

1. Download and extract FootageReviewer with **Code → Download ZIP**, as described above. Keep the extracted folder.
2. Install the **.NET 8 SDK** from [Microsoft's download page](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Choose **macOS Arm64** for Apple Silicon (M-series), or **macOS x64** for an Intel Mac. **Apple menu → About This Mac** shows which you have.
3. Follow the installation instructions on the [Homebrew website](https://brew.sh/), including the “Next steps” it prints. Open a new Terminal window afterward.
4. Paste this command into Terminal and press **Return** to install the playback and audio tools:

   ```sh
   brew install mpv ffmpeg
   ```

5. In Terminal, type **`bash` followed by a space**, then drag **`Build macOS app.command`** from your FootageReviewer folder into that Terminal window. Press **Return** and wait for the build to finish.
6. In Finder, open the **`dist`** folder inside your FootageReviewer folder, then open **`FootageReviewer.app`**.

The app uses the Homebrew libraries installed in step 4, so copying the `.app` to another Mac alone is not a complete installation. Start with playback and typed notes. The Windows transcription commands above do not apply on a Mac; Apple Silicon transcription uses `engine.macos.example.json` and the separate `transcribe_mlx.py` script and currently needs a more technical setup.

## Troubleshooting

| What you see | What to check |
| --- | --- |
| GitHub shows “404” or you cannot see the files | While this repository is private, sign in to an account that has been given access. |
| You cannot find the `.cmd` files | Open the extracted folder containing `README.md` and `src`. File Explorer may hide the `.cmd` ending; the files may appear as **Rebuild dist exe** and **Launch FootageReviewer**. |
| “dotnet is not recognized” or “No .NET SDKs were found” | Install the **.NET 8 SDK**, not just the Runtime. Close the build window and reopen the rebuild script after installation. |
| The launcher says it cannot find `FootageReviewer.exe` | Run **Rebuild dist exe.cmd** first. If it fails, read the first error above the final “Done” message. |
| An error mentions `libmpv-2.dll`, or playback will not start | Check for the **64-bit** `libmpv-2.dll` in `native/win-x64`, then rebuild. It should also appear beside the executable in `dist`. |
| Thumbnails or audio waveforms do not appear | Check that `ffmpeg.exe` and `ffprobe.exe` are directly inside `native/win-x64`, then rebuild. |
| “Transcription engine isn't installed yet” | Complete the optional Windows transcription section. The app does not install the engine automatically despite the wording of that message. |
| “py is not recognized” or Python 3.13 cannot be found | Install Python 3.13 with its launcher enabled, then open a new Command Prompt. |
| An optional setup command says a file cannot be found | Open Command Prompt from the FootageReviewer folder's address bar, not from inside `native` or `dist`. |
| Transcription is taking a long time | The first run downloads a model. CPU transcription can also be slow; try a short video first. |
| A build cannot download its components | Check your internet connection and retry the build. If your network blocks the download, save the first error message when asking for help. |

For help, use this repository's [Issues page](https://github.com/martincitopants-real/FootageReviewer-public/issues) and include your operating system and the error text. Remove personal folder names from error messages or screenshots before posting them.

## Updating later

Save your project and close FootageReviewer. Download the new source ZIP into a **new folder** and repeat the Windows setup there; you can copy your existing three files from `native/win-x64` to the new folder. Keep the old installation until the new one works.

If you use transcription, run its setup again in the new folder so Python's saved location stays correct. Do not copy an old `engine.json` by itself or move its `.venv` folder. Open your existing `.frproj` using **Open project…**; your original videos should stay in their existing location.
