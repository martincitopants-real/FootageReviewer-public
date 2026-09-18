using System;

namespace FootageReviewer.App.Util;

/// <summary>
/// Filename / folder splitting that works on a path from EITHER platform. <see cref="System.IO.Path"/>
/// only recognises the host's separators, so on macOS a Windows path like <c>Z:\a\b.mp4</c> has the
/// "filename" <c>Z:\a\b.mp4</c> — which is exactly what a shared project written on the PC contains.
/// </summary>
public static class PathText
{
    private static int LastSep(string p) => Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));

    /// <summary>Last segment of the path, whichever separator it uses.</summary>
    public static string FileName(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        var i = LastSep(p);
        return i < 0 ? p : p.Substring(i + 1);
    }

    /// <summary>Everything before the last segment (no trailing separator), or "" if there is none.</summary>
    public static string DirectoryName(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        var i = LastSep(p);
        return i <= 0 ? "" : p.Substring(0, i);
    }
}
