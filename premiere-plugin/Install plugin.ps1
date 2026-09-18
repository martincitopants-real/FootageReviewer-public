# Installs the FootageReviewer → Premiere bridge panel.
#
# Two steps, both per-user (nothing system-wide, no admin needed):
#   1. Copy the panel into Adobe's per-user CEP extensions folder.
#   2. Set PlayerDebugMode=1 under HKCU\Software\Adobe\CSXS.* — Adobe requires this to load an extension
#      that isn't signed with a paid Adobe certificate. It only affects YOUR user's Adobe apps.
#
# Run this with Premiere CLOSED, then start Premiere and open:
#   Window ▸ Extensions ▸ FootageReviewer Paste

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'com.footagereviewer.paste'
if (-not (Test-Path $src)) { throw "Can't find the panel folder next to this script: $src" }

$dest = Join-Path $env:APPDATA 'Adobe\CEP\extensions\com.footagereviewer.paste'
Write-Host "Installing panel -> $dest"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force

# Unsigned-extension switch. CSXS version varies by Premiere release, so set the ones in current use.
foreach ($v in 9..12) {
    $key = "HKCU:\Software\Adobe\CSXS.$v"
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name 'PlayerDebugMode' -Value '1' -Type String
    Write-Host "PlayerDebugMode=1 set for CSXS.$v"
}

Write-Host ""
Write-Host "Done. Start Premiere and open:  Window > Extensions > FootageReviewer Paste" -ForegroundColor Green
Write-Host "Then in FootageReviewer: press X, aim with the mouse, wheel to size, C to copy," -ForegroundColor Green
Write-Host "and click 'Paste section at playhead' in the Premiere panel." -ForegroundColor Green
