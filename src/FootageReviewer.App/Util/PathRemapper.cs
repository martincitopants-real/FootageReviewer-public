using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FootageReviewer.App.Util;

/// <summary>One per-machine media location: a prefix in the shared project mapped to a local folder.</summary>
public sealed class PathRemap
{
    /// <summary>Prefix as written in the project, e.g. <c>Z:\Footage\Session 2</c>.</summary>
    public string From { get; set; } = "";

    /// <summary>Where that footage lives on this machine.</summary>
    public string To { get; set; } = "";
}

/// <summary>
/// Translates between the paths a shared project stores and where the footage actually is on this
/// machine. The project keeps the canonical (usually NAS / Windows) paths; each machine holds its own
/// mappings in settings, applies them at load, and reverses them at save. Nothing machine-specific
/// ever reaches the .frproj, so the same file opens correctly on every machine that has a copy.
///
/// Prefix substitution rather than filename matching: it preserves folder structure exactly, which
/// day markers depend on, and it cannot confuse two same-named clips in different folders.
/// </summary>
public static class PathRemapper
{
    // Compare with separators unified. Case-insensitive because the canonical side is Windows.
    private static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');
    private static char SepOf(string p) => p.Contains('\\') ? '\\' : '/';

    /// <summary>Canonical → local, if a mapping applies AND the file exists there.</summary>
    public static bool TryLocalize(string canonical, IEnumerable<PathRemap>? remaps, out string local)
    {
        local = canonical;
        if (string.IsNullOrEmpty(canonical) || remaps == null) return false;
        var nc = Norm(canonical);
        foreach (var r in remaps)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;
            var nf = Norm(r.From);
            if (nc.Length <= nf.Length + 1) continue;
            if (!nc.StartsWith(nf, StringComparison.OrdinalIgnoreCase) || nc[nf.Length] != '/') continue;
            // Separators changed 1:1, so slicing the normalised string keeps the remainder's casing.
            var rest = nc.Substring(nf.Length + 1).Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.Combine(r.To, rest);
            if (File.Exists(candidate)) { local = candidate; return true; }
        }
        return false;
    }

    /// <summary>Local → canonical, if the file sits under a mapped local folder. No existence check.</summary>
    public static bool TryCanonicalize(string local, IEnumerable<PathRemap>? remaps, out string canonical)
    {
        canonical = local;
        if (string.IsNullOrEmpty(local) || remaps == null) return false;
        var nl = Norm(local);
        foreach (var r in remaps)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;
            var nt = Norm(r.To);
            if (nl.Length <= nt.Length + 1) continue;
            if (!nl.StartsWith(nt, StringComparison.OrdinalIgnoreCase) || nl[nt.Length] != '/') continue;
            var sep = SepOf(r.From);
            var rest = nl.Substring(nt.Length + 1).Replace('/', sep);
            canonical = r.From.TrimEnd('\\', '/') + sep + rest;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Derive one mapping from a set of relinked (old, new) pairs by stripping the longest common
    /// trailing segments. Returns null unless every pair agrees — a folder relinked by filename into a
    /// different structure is not a prefix move, and guessing would poison every later open.
    /// </summary>
    public static PathRemap? Infer(IReadOnlyList<(string Old, string New)> pairs)
    {
        PathRemap? result = null;
        foreach (var (o, n) in pairs)
        {
            if (string.IsNullOrEmpty(o) || string.IsNullOrEmpty(n)) return null;
            var os = Norm(o).Split('/');
            var ns = Norm(n).Split('/');
            var k = 0;
            while (k < os.Length - 1 && k < ns.Length - 1
                   && string.Equals(os[^(k + 1)], ns[^(k + 1)], StringComparison.OrdinalIgnoreCase)) k++;
            if (k == 0) return null; // not even the filename matched

            var cand = new PathRemap { From = Prefix(o, os.Length - k), To = Prefix(n, ns.Length - k) };
            if (result == null) result = cand;
            else if (!string.Equals(result.From, cand.From, StringComparison.OrdinalIgnoreCase)
                  || !string.Equals(result.To, cand.To, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return result;
    }

    /// <summary>The first <paramref name="segments"/> path segments of <paramref name="p"/>, in its own separator.</summary>
    private static string Prefix(string p, int segments)
    {
        var sep = SepOf(p);
        var parts = p.Replace('\\', '/').TrimEnd('/').Split('/');
        return string.Join(sep, parts.Take(segments));
    }

    public static bool Same(PathRemap a, PathRemap b)
        => string.Equals(Norm(a.From), Norm(b.From), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Norm(a.To), Norm(b.To), StringComparison.OrdinalIgnoreCase);
}
