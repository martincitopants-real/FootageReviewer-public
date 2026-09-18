using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace FootageReviewer.App.Util;

/// <summary>One project's launcher card. Everything here is cheap to show once cached.</summary>
public sealed class ProjectCard
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public DateTime LastOpenedUtc { get; set; }
    public int ClipCount { get; set; } = -1;      // -1 = not indexed yet
    public int LogCount { get; set; } = -1;
    public double DurationSeconds { get; set; }   // 0 = unknown
    public string? ThumbnailPath { get; set; }
    public bool Reachable { get; set; } = true;   // false = file missing (NAS offline / moved)

    public string SizeText => FileSizeBytes <= 0 ? "" : FileSizeBytes >= 1L << 20
        ? $"{FileSizeBytes / (double)(1L << 20):0.#} MB" : $"{FileSizeBytes / 1024.0:0} KB";

    public string DurationText
    {
        get
        {
            if (DurationSeconds <= 0) return "";
            var ts = TimeSpan.FromSeconds(DurationSeconds);
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m" : $"{ts.Minutes}m";
        }
    }

    public string LastOpenedText
    {
        get
        {
            if (LastOpenedUtc == default) return "";
            var d = DateTime.UtcNow - LastOpenedUtc;
            if (d.TotalMinutes < 2) return "just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
            if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
            return LastOpenedUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>"57 clips - 12h 3m - 726 logs", using only the parts we actually know.</summary>
    public string StatsText
    {
        get
        {
            var parts = new List<string>();
            if (ClipCount >= 0) parts.Add($"{ClipCount} clip{(ClipCount == 1 ? "" : "s")}");
            if (DurationSeconds > 0) parts.Add(DurationText);
            if (LogCount >= 0) parts.Add($"{LogCount} log{(LogCount == 1 ? "" : "s")}");
            return string.Join("  ·  ", parts);
        }
    }

    // Decoded lazily when the card is first shown, so building a list of stubs stays instant.
    private Bitmap? _thumb;
    private bool _thumbLoaded;
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbLoaded) return _thumb;
            _thumbLoaded = true;
            try { if (!string.IsNullOrEmpty(ThumbnailPath) && File.Exists(ThumbnailPath)) _thumb = new Bitmap(ThumbnailPath); }
            catch { _thumb = null; }
            return _thumb;
        }
    }

    public string SubtitleText
    {
        get
        {
            var bits = new List<string>();
            if (!string.IsNullOrEmpty(LastOpenedText)) bits.Add(LastOpenedText);
            if (!string.IsNullOrEmpty(SizeText)) bits.Add(SizeText);
            return string.Join("  ·  ", bits);
        }
    }
}

/// <summary>
/// Builds the launcher's project cards. Reading a .frproj is expensive (they run to tens of MB once
/// thumbnails/waveforms/transcript are embedded, often over a NAS), so everything derived from the file is
/// cached under %LOCALAPPDATA%\FootageReviewer\cache\projects and only recomputed when the project's size or
/// modified-time changes.
/// </summary>
public static class ProjectIndex
{
    private static string CacheDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FootageReviewer", "cache", "projects");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private sealed class CacheEntry
    {
        public long SourceSize { get; set; }
        public long SourceTicks { get; set; }
        public int ClipCount { get; set; } = -1;
        public int LogCount { get; set; } = -1;
        public double DurationSeconds { get; set; }
        public DateTime LastOpenedUtc { get; set; }
    }

    private static string KeyFor(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 10);
    }

    private static string JsonPathFor(string path) => System.IO.Path.Combine(CacheDir, KeyFor(path) + ".json");
    private static string ThumbPathFor(string path) => System.IO.Path.Combine(CacheDir, KeyFor(path) + ".jpg");

    /// <summary>Instant card from the path alone - no project file read. Paints the list immediately.</summary>
    public static ProjectCard Stub(string path)
    {
        var card = new ProjectCard
        {
            Path = path,
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Folder = System.IO.Path.GetDirectoryName(path) ?? "",
        };
        try
        {
            var fi = new FileInfo(path);
            card.Reachable = fi.Exists;
            if (fi.Exists) { card.FileSizeBytes = fi.Length; card.LastOpenedUtc = fi.LastWriteTimeUtc; }
        }
        catch { card.Reachable = false; }

        // Show whatever we already know while the full read happens (and when the file is unreachable).
        if (TryReadCache(path, out var c) && c != null)
        {
            card.ClipCount = c.ClipCount;
            card.LogCount = c.LogCount;
            card.DurationSeconds = c.DurationSeconds;
            if (c.LastOpenedUtc != default) card.LastOpenedUtc = c.LastOpenedUtc;
        }
        if (File.Exists(ThumbPathFor(path))) card.ThumbnailPath = ThumbPathFor(path);
        return card;
    }

    private static bool TryReadCache(string path, out CacheEntry? entry)
    {
        entry = null;
        try
        {
            var f = JsonPathFor(path);
            if (!File.Exists(f)) return false;
            entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(f), Json);
            return entry != null;
        }
        catch { return false; }
    }

    /// <summary>True when the cache still matches the project on disk, so no re-read is needed.</summary>
    public static bool IsCacheFresh(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return true; // can't refresh an unreachable project - keep what we have
            if (!TryReadCache(path, out var c) || c == null) return false;
            return c.SourceSize == fi.Length && c.SourceTicks == fi.LastWriteTimeUtc.Ticks
                   && c.ClipCount >= 0 && File.Exists(ThumbPathFor(path));
        }
        catch { return false; }
    }

    /// <summary>Record that a project was just opened (drives "last opened" on the card).</summary>
    public static void TouchOpened(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            Directory.CreateDirectory(CacheDir);
            TryReadCache(path, out var c);
            c ??= new CacheEntry();
            c.LastOpenedUtc = DateTime.UtcNow;
            File.WriteAllText(JsonPathFor(path), JsonSerializer.Serialize(c, Json));
        }
        catch { /* best effort */ }
    }

    /// <summary>Forget a project's cached card (used when it's removed from the recent list).</summary>
    public static void Forget(string path)
    {
        try { File.Delete(JsonPathFor(path)); } catch { }
        try { File.Delete(ThumbPathFor(path)); } catch { }
    }

    /// <summary>
    /// Full read: opens the project, pulls counts/duration and extracts a poster frame. This is the multi-MB
    /// decompress, so callers run it off the UI thread. The result is cached for next time.
    /// </summary>
    public static ProjectCard Index(string path)
    {
        var card = Stub(path);
        if (!card.Reachable) return card;
        try
        {
            using var doc = ReadProject(path);
            var root = doc.RootElement;

            card.ClipCount = root.TryGetProperty("Sources", out var src) && src.ValueKind == JsonValueKind.Array
                ? src.GetArrayLength() : 0;
            card.LogCount = root.TryGetProperty("Entries", out var ent) && ent.ValueKind == JsonValueKind.Array
                ? ent.GetArrayLength() : 0;

            var playhead = root.TryGetProperty("PlayheadSeconds", out var ph) && ph.TryGetDouble(out var phv) ? phv : 0;
            card.DurationSeconds = TotalDuration(root, out var clipDurations);
            var poster = ExtractPoster(root, clipDurations, playhead, path);
            if (poster != null) card.ThumbnailPath = poster;

            var fi = new FileInfo(path);
            TryReadCache(path, out var prev);
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(JsonPathFor(path), JsonSerializer.Serialize(new CacheEntry
            {
                SourceSize = fi.Length,
                SourceTicks = fi.LastWriteTimeUtc.Ticks,
                ClipCount = card.ClipCount,
                LogCount = card.LogCount,
                DurationSeconds = card.DurationSeconds,
                LastOpenedUtc = prev?.LastOpenedUtc ?? card.LastOpenedUtc,
            }, Json));
            if (prev != null && prev.LastOpenedUtc != default) card.LastOpenedUtc = prev.LastOpenedUtc;
        }
        catch { /* leave the stub as-is; a broken project still gets a card */ }
        return card;
    }

    private static JsonDocument ReadProject(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var magic = new byte[2];
        var read = fs.Read(magic, 0, 2);
        fs.Position = 0;
        if (read == 2 && magic[0] == 0x1F && magic[1] == 0x8B)
        {
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            ms.Position = 0;
            return JsonDocument.Parse(ms);
        }
        return JsonDocument.Parse(fs);
    }

    /// <summary>
    /// Per-clip durations without touching the footage: the embedded waveform is one byte per peak at a fixed
    /// 32 buckets/second, so its length IS the clip's length. Base64 grows by 4/3, so the ENCODED string's
    /// length alone gives the duration - no decode needed.
    /// </summary>
    private static double TotalDuration(JsonElement root, out List<double> perClip)
    {
        const double bucketsPerSec = 32.0;
        perClip = new List<double>();
        if (!root.TryGetProperty("ClipAssets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return 0;
        foreach (var a in assets.EnumerateArray())
        {
            var best = 0.0;
            if (a.TryGetProperty("WaveU8", out var waves) && waves.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in waves.EnumerateArray())
                {
                    if (w.ValueKind != JsonValueKind.String) continue;
                    var b64 = w.GetString();
                    if (string.IsNullOrEmpty(b64)) continue;
                    var bytes = b64.Length / 4.0 * 3.0; // approx decoded length
                    best = Math.Max(best, bytes / bucketsPerSec);
                }
            }
            perClip.Add(best);
        }
        return perClip.Sum();
    }

    /// <summary>Poster frame: the embedded thumbnail nearest where you left off, written out as a small JPEG.</summary>
    private static string? ExtractPoster(JsonElement root, List<double> clipDurations, double playhead, string path)
    {
        if (!root.TryGetProperty("ClipAssets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        // Which clip is the playhead in? (cumulative starts from the derived durations)
        var target = 0;
        var acc = 0.0;
        for (var i = 0; i < clipDurations.Count; i++)
        {
            if (playhead < acc + clipDurations[i]) { target = i; break; }
            acc += clipDurations[i];
            target = i;
        }
        var inClip = Math.Max(0, playhead - acc);

        var list = assets.EnumerateArray().ToList();
        foreach (var idx in Order(target, list.Count))
        {
            var a = list[idx];
            if (!a.TryGetProperty("ThumbnailsB64", out var thumbs) || thumbs.ValueKind != JsonValueKind.Array) continue;
            var frames = thumbs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).ToList();
            if (frames.Count == 0) continue;

            var pick = 0;
            if (idx == target && a.TryGetProperty("ThumbnailTimes", out var times) && times.ValueKind == JsonValueKind.Array)
            {
                var tl = times.EnumerateArray().Select(x => x.TryGetDouble(out var v) ? v : 0).ToList();
                var bestDiff = double.MaxValue;
                for (var i = 0; i < tl.Count && i < frames.Count; i++)
                {
                    var diff = Math.Abs(tl[i] - inClip);
                    if (diff < bestDiff) { bestDiff = diff; pick = i; }
                }
            }
            try
            {
                var s = frames[Math.Min(pick, frames.Count - 1)].GetString();
                if (string.IsNullOrEmpty(s)) continue;
                var bytes = Convert.FromBase64String(s);
                Directory.CreateDirectory(CacheDir);
                var outPath = ThumbPathFor(path);
                File.WriteAllBytes(outPath, bytes);
                return outPath;
            }
            catch { /* try the next clip */ }
        }
        return null;
    }

    /// <summary>Clip indices to try for a poster: the playhead's clip first, then outward.</summary>
    private static IEnumerable<int> Order(int start, int count)
    {
        if (count <= 0) yield break;
        start = Math.Clamp(start, 0, count - 1);
        yield return start;
        for (var d = 1; d < count; d++)
        {
            if (start + d < count) yield return start + d;
            if (start - d >= 0) yield return start - d;
        }
    }
}
