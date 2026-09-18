using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FootageReviewer.App.Util;

/// <summary>
/// Builds an FCP7 XML ("xmeml") sequence for a selected span of the review timeline.
///
/// Premiere's own timeline clipboard is a private, undocumented format that a third-party app can't
/// synthesise, so there is no way to literally Ctrl+V a clip into its timeline. xmeml is the supported
/// interchange path: Premiere reads it via File ▸ Import (and accepts the .xml pasted/dropped into the
/// Project panel), resolves the media by the absolute path we write, and produces a sequence containing
/// exactly the selected range — which is then dragged into the edit.
/// </summary>
public static class PremiereXml
{
    public sealed class Piece
    {
        public required string Path;      // absolute path of the source file
        public required double SourceIn;  // seconds into that file
        public required double SourceOut;
        public required double FileDuration; // seconds (whole file), used for the <file> duration
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>file:// URL Premiere expects in &lt;pathurl&gt; (absolute, forward slashes, %-escaped).</summary>
    private static string PathUrl(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        var uri = new UriBuilder("file", "localhost") { Path = "/" + full.TrimStart('/') }.Uri;
        return uri.AbsoluteUri;
    }

    /// <summary>
    /// One sequence, laid out end-to-end from <paramref name="pieces"/> (a selection can span several
    /// recordings). Video on V1 with the matching audio on A1 linked to it.
    /// </summary>
    public static string BuildSequence(IReadOnlyList<Piece> pieces, double fps, int width, int height, string sequenceName)
    {
        if (fps <= 0) fps = 60;
        var tb = (int)Math.Round(fps);
        var ntsc = Math.Abs(fps - tb) > 0.001 ? "TRUE" : "FALSE"; // 59.94 etc. are timebase 60 + NTSC
        int F(double sec) => Math.Max(0, (int)Math.Round(sec * fps));

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE xmeml>");
        sb.AppendLine("<xmeml version=\"4\">");
        sb.AppendLine("  <sequence id=\"fr-sequence-1\">");
        sb.AppendLine($"    <name>{Esc(sequenceName)}</name>");

        var total = 0;
        foreach (var p in pieces) total += Math.Max(1, F(p.SourceOut) - F(p.SourceIn));
        sb.AppendLine($"    <duration>{Inv(total)}</duration>");
        sb.AppendLine($"    <rate><timebase>{Inv(tb)}</timebase><ntsc>{ntsc}</ntsc></rate>");
        sb.AppendLine("    <media>");

        // ---- video ----
        sb.AppendLine("      <video>");
        sb.AppendLine("        <format><samplecharacteristics>");
        sb.AppendLine($"          <rate><timebase>{Inv(tb)}</timebase><ntsc>{ntsc}</ntsc></rate>");
        sb.AppendLine($"          <width>{Inv(width)}</width><height>{Inv(height)}</height>");
        sb.AppendLine("        </samplecharacteristics></format>");
        sb.AppendLine("        <track>");
        AppendClipItems(sb, pieces, tb, ntsc, width, height, F, video: true);
        sb.AppendLine("        </track>");
        sb.AppendLine("      </video>");

        // ---- audio (one stereo track, linked to the video item so they move together) ----
        sb.AppendLine("      <audio>");
        sb.AppendLine("        <track>");
        AppendClipItems(sb, pieces, tb, ntsc, width, height, F, video: false);
        sb.AppendLine("        </track>");
        sb.AppendLine("      </audio>");

        sb.AppendLine("    </media>");
        sb.AppendLine("  </sequence>");
        sb.AppendLine("</xmeml>");
        return sb.ToString();
    }

    private static void AppendClipItems(StringBuilder sb, IReadOnlyList<Piece> pieces, int tb, string ntsc,
        int width, int height, Func<double, int> F, bool video)
    {
        var cursor = 0;
        for (var i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            var inF = F(p.SourceIn);
            var outF = Math.Max(inF + 1, F(p.SourceOut));
            var len = outF - inF;
            var name = Path.GetFileName(p.Path);
            var kind = video ? "v" : "a";
            var fileId = $"fr-file-{i + 1}";

            sb.AppendLine($"          <clipitem id=\"fr-{kind}-{i + 1}\">");
            sb.AppendLine($"            <name>{Esc(name)}</name>");
            sb.AppendLine($"            <duration>{Inv(F(p.FileDuration))}</duration>");
            sb.AppendLine($"            <rate><timebase>{Inv(tb)}</timebase><ntsc>{ntsc}</ntsc></rate>");
            sb.AppendLine($"            <start>{Inv(cursor)}</start><end>{Inv(cursor + len)}</end>");
            sb.AppendLine($"            <in>{Inv(inF)}</in><out>{Inv(outF)}</out>");

            // Emit the full <file> once (on the video item); the audio item just references the same id,
            // which is how FCP7 XML avoids duplicating media definitions.
            if (video)
            {
                sb.AppendLine($"            <file id=\"{fileId}\">");
                sb.AppendLine($"              <name>{Esc(name)}</name>");
                sb.AppendLine($"              <pathurl>{Esc(PathUrl(p.Path))}</pathurl>");
                sb.AppendLine($"              <rate><timebase>{Inv(tb)}</timebase><ntsc>{ntsc}</ntsc></rate>");
                sb.AppendLine($"              <duration>{Inv(F(p.FileDuration))}</duration>");
                sb.AppendLine("              <media>");
                sb.AppendLine("                <video><samplecharacteristics>");
                sb.AppendLine($"                  <rate><timebase>{Inv(tb)}</timebase><ntsc>{ntsc}</ntsc></rate>");
                sb.AppendLine($"                  <width>{Inv(width)}</width><height>{Inv(height)}</height>");
                sb.AppendLine("                </samplecharacteristics></video>");
                sb.AppendLine("                <audio><channelcount>2</channelcount></audio>");
                sb.AppendLine("              </media>");
                sb.AppendLine("            </file>");
            }
            else
            {
                sb.AppendLine($"            <file id=\"{fileId}\"/>");
                sb.AppendLine("            <sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>");
            }

            // Link the video + audio halves so dragging one takes the other.
            sb.AppendLine("            <link><linkclipref>fr-v-" + Inv(i + 1) + "</linkclipref><mediatype>video</mediatype></link>");
            sb.AppendLine("            <link><linkclipref>fr-a-" + Inv(i + 1) + "</linkclipref><mediatype>audio</mediatype></link>");
            sb.AppendLine("          </clipitem>");
            cursor += len;
        }
    }
}
