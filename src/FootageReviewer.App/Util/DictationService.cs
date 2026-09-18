using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace FootageReviewer.App.Util;

/// <summary>
/// Records the microphone to a temp WAV via the bundled ffmpeg (dshow), so it can be transcribed by the
/// whisper sidecar. Start() begins recording; Stop() finalizes the WAV cleanly (sends "q" to ffmpeg's
/// stdin — killing would truncate the header) and returns the file path. Enumeration parses ffmpeg's
/// device list (which it prints to stderr).
/// </summary>
public sealed class DictationService
{
    private Process? _proc;
    private string? _wavPath;

    public bool IsRecording => _proc is { HasExited: false };

    /// <summary>List the system's audio capture device names (for the Settings dropdown).</summary>
    public static List<string> EnumerateMicrophones(string ffmpegPath)
    {
        var list = new List<string>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" })
                psi.ArgumentList.Add(a);
            using var p = ChildProcesses.Start(psi);
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(5000); // ffmpeg exits non-zero here by design; ignore the code
            // Handle BOTH ffmpeg formats: older builds print a "DirectShow audio devices" section header;
            // newer builds (5.x+) tag each device line with a trailing "(audio)" / "(video)" and have no header.
            var audioSection = false;
            foreach (var raw in err.Split('\n'))
            {
                var line = raw;
                if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase)) continue;
                // Section headers: check VIDEO first and anchor to the full phrase — the classic video header
                // is "DirectShow video devices (some may be both video and audio devices)", so a loose
                // "audio devices" match would wrongly flip into audio mode and list webcams as mics.
                if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase)) { audioSection = false; continue; }
                if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase)) { audioSection = true; continue; }
                // Per-line tag (new format): anchor to END of line so a device NAME containing "(audio)"
                // doesn't override its real "(video)" tag.
                var t = line.TrimEnd();
                var isAudio = t.EndsWith("(audio)", StringComparison.OrdinalIgnoreCase);
                var isVideo = t.EndsWith("(video)", StringComparison.OrdinalIgnoreCase);
                var m = Regex.Match(line, "\"([^\"]+)\"");
                if (!m.Success) continue;
                if (isAudio || (audioSection && !isVideo)) list.Add(m.Groups[1].Value);
            }
        }
        catch { /* return whatever we got */ }
        return list;
    }

    /// <summary>The dshow device name in <paramref name="mics"/> that matches the Windows default capture
    /// endpoint (Core Audio friendly name), or null if it can't be determined / matched. dshow device names
    /// usually equal the MMDevice friendly name; we also accept a prefix match to survive minor truncation.</summary>
    public static string? MatchDefaultDevice(IReadOnlyList<string> mics)
    {
        var def = WindowsAudio.GetDefaultCaptureName();
        if (string.IsNullOrWhiteSpace(def) || mics.Count == 0) return null;
        foreach (var m in mics) if (string.Equals(m, def, StringComparison.OrdinalIgnoreCase)) return m;
        // Loose prefix match (handles ffmpeg name truncation) — but only when UNAMBIGUOUS, so near-duplicate
        // names ("Mic (USB Audio)" vs "Mic (USB Audio) #2") never bind to the wrong device.
        string? only = null;
        foreach (var m in mics)
        {
            if (def.StartsWith(m, StringComparison.OrdinalIgnoreCase) ||
                m.StartsWith(def, StringComparison.OrdinalIgnoreCase))
            {
                if (only != null) return null; // ambiguous → caller falls back to the first device
                only = m;
            }
        }
        return only;
    }

    /// <summary>Begin recording. <paramref name="device"/> null/empty → the Windows default mic.</summary>
    public bool Start(string ffmpegPath, string? device)
    {
        if (IsRecording) return false;
        try
        {
            var dev = device;
            if (string.IsNullOrWhiteSpace(dev))
            {
                var mics = EnumerateMicrophones(ffmpegPath);
                if (mics.Count == 0) return false;
                // "Windows default" → record from the ACTUAL Windows default capture device (matched to a
                // dshow name), not just whatever ffmpeg enumerates first. Falls back to the first device.
                dev = MatchDefaultDevice(mics) ?? mics[0];
            }
            _wavPath = Path.Combine(Path.GetTempPath(), $"fr_dictate_{Guid.NewGuid():N}.wav");
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            // audio=<name> must be ONE arg with no manual quotes (.NET quotes args containing spaces).
            foreach (var a in new[] { "-hide_banner", "-f", "dshow", "-i", $"audio={dev}",
                                      "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", _wavPath })
                psi.ArgumentList.Add(a);
            _proc = ChildProcesses.Start(psi);
            _ = _proc.StandardError.ReadToEndAsync(); // drain so the pipe never blocks ffmpeg
            return true;
        }
        catch { _proc = null; _wavPath = null; return false; }
    }

    /// <summary>Stop recording cleanly and return the finalized WAV path (null if nothing usable).</summary>
    public string? Stop()
    {
        var proc = _proc;
        var wav = _wavPath;
        _proc = null;
        _wavPath = null;
        if (proc == null) return null;
        try
        {
            if (!proc.HasExited)
            {
                try { proc.StandardInput.Write("q"); proc.StandardInput.Flush(); proc.StandardInput.Close(); }
                catch { /* fall through to wait/kill */ }
                if (!proc.WaitForExit(3000)) { try { proc.Kill(true); } catch { } }
            }
        }
        catch { /* ignore */ }
        finally { try { proc.Dispose(); } catch { } }

        try
        {
            if (wav != null && File.Exists(wav) && new FileInfo(wav).Length > 1024) return wav;
        }
        catch { /* ignore */ }
        // Unusable (too small / unreadable) → delete the orphan temp file rather than leaking it.
        if (wav != null) try { File.Delete(wav); } catch { /* ignore */ }
        return null;
    }
}
