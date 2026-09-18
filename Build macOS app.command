#!/bin/bash
# Build the macOS .app bundle. Mirrors "Rebuild dist exe.cmd" on Windows.
# Requires: dotnet 8, and "brew install mpv ffmpeg" for playback / thumbnails / waveforms.
set -euo pipefail
cd "$(dirname "$0")"

export PATH="/opt/homebrew/opt/dotnet@8/bin:/opt/homebrew/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ARCH="$(uname -m)"; [ "$ARCH" = "arm64" ] && RID=osx-arm64 || RID=osx-x64
APP="dist/FootageReviewer.app"

echo "==> publishing ($RID)"
rm -rf "dist/$RID" "$APP"
# Self-contained: Homebrew's dotnet@8 is keg-only, so a framework-dependent apphost cannot find
# libhostfxr when the app is launched from Finder (which inherits no shell PATH/DOTNET_ROOT).
# Bundling the runtime makes the .app double-clickable with no .NET install required.
dotnet publish -c Release -r "$RID" --self-contained true \
  src/FootageReviewer.App/FootageReviewer.App.csproj -o "dist/$RID"

echo "==> assembling $APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "dist/$RID/." "$APP/Contents/MacOS/"

# Whisper sidecar (transcribe.py + engine config) — WhisperFile() looks for <appdir>/whisper first.
mkdir -p "$APP/Contents/MacOS/whisper"
cp native/whisper/transcribe.py "$APP/Contents/MacOS/whisper/" 2>/dev/null || true
cp native/whisper/transcribe_mlx.py "$APP/Contents/MacOS/whisper/" 2>/dev/null || true
cp native/whisper/engine.macos.json "$APP/Contents/MacOS/whisper/" 2>/dev/null || true

# Icon: the repo ships a Windows .ico; convert it to .icns when the tools are available.
if command -v sips >/dev/null && command -v iconutil >/dev/null; then
  ICONSET="$(mktemp -d)/FootageReviewer.iconset"; mkdir -p "$ICONSET"
  if sips -s format png "src/FootageReviewer.App/Assets/icon.ico" --out "$ICONSET/base.png" >/dev/null 2>&1; then
    for sz in 16 32 64 128 256 512; do
      sips -z $sz $sz "$ICONSET/base.png" --out "$ICONSET/icon_${sz}x${sz}.png" >/dev/null 2>&1 || true
      sips -z $((sz*2)) $((sz*2)) "$ICONSET/base.png" --out "$ICONSET/icon_${sz}x${sz}@2x.png" >/dev/null 2>&1 || true
    done
    rm -f "$ICONSET/base.png"
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/FootageReviewer.icns" 2>/dev/null || true
  fi
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>              <string>FootageReviewer</string>
  <key>CFBundleDisplayName</key>       <string>FootageReviewer</string>
  <key>CFBundleIdentifier</key>        <string>com.footagereviewer.app</string>
  <key>CFBundleVersion</key>           <string>1.15.1</string>
  <key>CFBundleShortVersionString</key><string>1.15.1</string>
  <key>CFBundlePackageType</key>       <string>APPL</string>
  <key>CFBundleExecutable</key>        <string>FootageReviewer.App</string>
  <key>CFBundleIconFile</key>          <string>FootageReviewer</string>
  <key>NSHighResolutionCapable</key>   <true/>
  <key>LSMinimumSystemVersion</key>    <string>12.0</string>
  <key>NSMicrophoneUsageDescription</key>
  <string>FootageReviewer records your microphone for the dictation feature.</string>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/FootageReviewer.App"
# Ad-hoc sign so Gatekeeper lets the unsigned bundle run locally.
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "   (codesign skipped)"

echo "==> done: $APP"
echo "    open '$APP'"
