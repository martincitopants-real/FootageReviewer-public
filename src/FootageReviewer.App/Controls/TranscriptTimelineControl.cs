using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FootageReviewer.App.Models;

namespace FootageReviewer.App.Controls;

/// <summary>
/// Vertical transcript timeline — one COLUMN per audio track, time flowing top-to-bottom.
/// Each segment is a block in its column at its time position; empty space where nothing's said.
///
///   • Click a segment → seek to its Start.
///   • Click empty space in a column → scrub the playhead to that time.
///   • Right-click a segment → context menu (Copy text, Add to manual log).
///   • Alt + scroll → zoom the vertical time axis around the cursor.
///   • Scroll → pan the time axis.
///   • Click a column header → focus that column (collapse others). Click again to restore.
/// </summary>
public sealed class TranscriptTimelineControl : Control
{
    public const double HeaderHeight = 22;
    public const double CopyButtonSize = 16;
    public const double RightRulerWidth = 52;   // vertical time ruler down the right edge

    // Usable width for the track columns (everything left of the time ruler).
    private double ContentWidth => Math.Max(0, Bounds.Width - RightRulerWidth);

    // viewport — _pps is pixels per second VERTICALLY. _scroll is seconds at y = HeaderHeight.
    private double _total;
    private double _playhead;
    private double _pps = 10;
    private double _scroll;
    private bool _fitted;

    // tracks
    private int _trackCount;
    private string[] _titles = Array.Empty<string>();
    private List<TranscriptSegment>[] _byTrack = Array.Empty<List<TranscriptSegment>>();
    private int _focusedTrack = -1;
    private string _search = "";
    private bool[] _hidden = Array.Empty<bool>();   // per-track: hide that column's bubbles (column stays)
    private bool[] _collapsed = Array.Empty<bool>(); // per-track: hide the whole column (others fill the gap)
    private double[] _weights = Array.Empty<double>(); // per-track relative column width (drag dividers)
    private double _fontSize = 11;                   // transcript bubble body font size (Settings)

    // column-divider drag state
    private bool _draggingDivider;
    private int _divLeft = -1, _divRight = -1;
    private double _divStartX, _divLeftW0, _divRightW0, _divCombinedWeight0;
    private bool _hoverDivider;
    private const double MinColWidth = 28;
    private const double MinWeight = 0.05;   // floor for a column's relative width (store + read must match)
    private double[] _markers = Array.Empty<double>();        // manual-log marker times (canonical video s)
    private string[] _markerColors = Array.Empty<string>();
    private string[] _markerTexts = Array.Empty<string>();    // log text, for the hover tooltip
    private int _hoveredMarker = -1;                          // marker whose left-edge tag is hovered
    private int _draggingMarker = -1;                         // marker being dragged (retimes its log entry)
    private bool _markerDidMove;                              // distinguishes a drag from a plain tag click
    private double _markerDragStartY;
    private const double MarkerDragThreshold = 4;             // px before a press becomes a drag
    private readonly Dictionary<string, IPen> _markerPenCache = new();
    private readonly Dictionary<string, IBrush> _markerBrushCache = new();

    // interaction state
    private int _hoverTrack = -1;
    private int _hoverSeg = -1;
    private bool _hoverCopyBtn;
    private bool _scrubbing;
    private long _lastSeekTick;
    private int _rightClickTrack = -1;
    private int _rightClickSeg = -1;
    // Copy-target highlight: while Ctrl is held, outline the bubbles Ctrl+1/2/3 would copy.
    private readonly HashSet<TranscriptSegment> _copyTargets = new();
    // Drawn-rect cache (populated each Render in DrawColumns) so hover/click hit-test the bubble's ACTUAL
    // rendered size (inflated to MinReadableHeight), not its raw time-span. Keyed by (track, segIdx).
    private readonly Dictionary<(int track, int seg), Rect> _drawnBubbles = new();
    private readonly Dictionary<(int track, int seg), Rect> _drawnCopyButtons = new();

    public event Action<double>? SeekRequested;
    /// <summary>Raised when a marker tag is dragged to a new time (marker index, new time) — retimes its log.</summary>
    public event Action<int, double>? MarkerMoved;
    /// <summary>Raised when the user wants to copy a segment (track index, segment).</summary>
    public event Action<int, TranscriptSegment>? SegmentCopyRequested;
    /// <summary>Raised when the user wants to add a segment as a manual-log entry.</summary>
    public event Action<TranscriptSegment>? SegmentLogRequested;
    /// <summary>Raised on every zoom/pan (kept for parity with the video timeline lock-zoom).</summary>
    public event Action<double, double>? ViewportChanged;
    /// <summary>Raised only on a genuine user zoom/pan (mouse wheel), not programmatic viewport changes.</summary>
    public event Action? ViewportUserChanged;

    private static ImmutableSolidColorBrush B(string c) => new(Color.Parse(c));
    private static IPen P(string c, double w = 1) => new ImmutablePen(B(c), w);

    private readonly IBrush _bg = B("#0E0E13");
    private readonly IBrush _headerBg = B("#16161D");
    private readonly IBrush _laneBg = B("#12121A");
    private readonly IBrush _laneBgAlt = B("#0F0F16");
    private readonly IBrush _seg = B("#274050");
    private readonly IBrush _segAlt = B("#203440");
    private readonly IBrush _segActive = B("#3F5A8A");
    private readonly IBrush _segHover = B("#33476E");
    private readonly IBrush _segText = B("#E2E2EA");
    private readonly IBrush _headerText = B("#BFD2F2");
    private readonly IBrush _headerTextFocused = B("#FFE0405A");
    private readonly IBrush _emptyHint = B("#5A5A66");
    private readonly IBrush _segMatch = B("#5A4A2E");
    private readonly IPen _segMatchBorder = P("#FFCB5C", 1.5);
    private readonly IBrush _copyBtnBg = B("#1B1B27");
    private readonly IBrush _copyBtnHover = B("#274050");
    private readonly IBrush _copyBtnText = B("#BFD2F2");
    private readonly IPen _segBorder = P("#66000000");
    private readonly IPen _copyBtnBorder = P("#88FFFFFF");
    private readonly IPen _laneSep = P("#33000000");
    private readonly IPen _play = P("#FFE0405A");
    private readonly IPen _copyTargetBorder = P("#FF8CE6A0", 2.5); // bright green outline for Ctrl-copy targets
    private readonly IBrush _hoverBg = B("#F01B1B25");   // marker hover tooltip (mirrors the video timeline)
    private readonly IPen _hoverBorder = P("#FFCB5C");
    private readonly IBrush _hoverText = B("#FFFFFF");
    private readonly Typeface _tf = new("Inter");
    private readonly IBrush _dayLabelText = new SolidColorBrush(Color.Parse("#12121A")); // dark on the day chip
    private readonly Typeface _tfHead = new(new FontFamily("Inter"), FontStyle.Normal, FontWeight.SemiBold);

    public TranscriptTimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
        MinWidth = 200;
        MinHeight = 200;

        // Right-click → context menu with Copy + Add-to-log.
        var menu = new ContextMenu();
        var copyItem = new MenuItem { Header = "Copy text" };
        copyItem.Click += (_, _) =>
        {
            var s = ResolveRightClicked();
            if (s != null) SegmentCopyRequested?.Invoke(_rightClickTrack, s);
        };
        var logItem = new MenuItem { Header = "Add to manual log" };
        logItem.Click += (_, _) =>
        {
            var s = ResolveRightClicked();
            if (s != null) SegmentLogRequested?.Invoke(s);
        };
        menu.Items.Add(copyItem);
        menu.Items.Add(logItem);
        ContextMenu = menu;
    }

    private TranscriptSegment? ResolveRightClicked()
    {
        if (_rightClickTrack < 0 || _rightClickTrack >= _byTrack.Length) return null;
        var col = _byTrack[_rightClickTrack];
        if (_rightClickSeg < 0 || _rightClickSeg >= col.Count) return null;
        return col[_rightClickSeg];
    }

    public void SetTracks(IReadOnlyList<string> titles)
    {
        _trackCount = titles.Count;
        _titles = new string[_trackCount];
        for (var i = 0; i < _trackCount; i++) _titles[i] = titles[i];
        _byTrack = new List<TranscriptSegment>[_trackCount];
        for (var i = 0; i < _trackCount; i++) _byTrack[i] = new List<TranscriptSegment>();
        _hidden = new bool[_trackCount];
        _collapsed = new bool[_trackCount];
        _weights = new double[_trackCount];
        for (var i = 0; i < _trackCount; i++) _weights[i] = 1.0;
        _focusedTrack = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Hide/show a track's bubbles (column background + header stay).</summary>
    public void SetTrackHidden(int trackIdx, bool hidden)
    {
        if (trackIdx < 0 || trackIdx >= _hidden.Length || _hidden[trackIdx] == hidden) return;
        _hidden[trackIdx] = hidden;
        InvalidateVisual();
    }

    /// <summary>Collapse a track's whole column; remaining tracks fill the space. Keeps ≥1 visible.</summary>
    public void SetTrackCollapsed(int trackIdx, bool collapsed)
    {
        if (trackIdx < 0 || trackIdx >= _collapsed.Length || _collapsed[trackIdx] == collapsed) return;
        if (collapsed && CountShownExcluding(trackIdx) == 0) return; // never hide the last visible track
        _collapsed[trackIdx] = collapsed;
        if (collapsed && _focusedTrack == trackIdx) _focusedTrack = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public bool IsTrackCollapsed(int trackIdx) =>
        trackIdx >= 0 && trackIdx < _collapsed.Length && _collapsed[trackIdx];

    public double[] GetTrackWeights() => (double[])_weights.Clone();
    public bool[] GetTrackCollapsed() => (bool[])_collapsed.Clone();

    /// <summary>Restore persisted column widths/collapse (by index, length-tolerant).</summary>
    public void RestoreLayout(IReadOnlyList<double>? weights, IReadOnlyList<bool>? collapsed)
    {
        if (weights != null)
            for (var i = 0; i < _weights.Length && i < weights.Count; i++)
                if (weights[i] >= MinWeight) _weights[i] = weights[i];
        if (collapsed != null)
            for (var i = 0; i < _collapsed.Length && i < collapsed.Count; i++)
                _collapsed[i] = collapsed[i];
        // Guard: don't leave every track collapsed.
        if (_trackCount > 0 && AllCollapsed()) _collapsed[0] = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Raised when the user resizes or collapses columns (so the owner can persist).</summary>
    public event Action? LayoutChanged;

    private int CountShownExcluding(int except)
    {
        var n = 0;
        for (var k = 0; k < _trackCount; k++) if (k != except && !Collapsed(k)) n++;
        return n;
    }
    private bool AllCollapsed()
    {
        for (var k = 0; k < _trackCount; k++) if (!_collapsed[k]) return false;
        return _trackCount > 0;
    }
    private double Weight(int k) => (k >= 0 && k < _weights.Length && _weights[k] >= MinWeight) ? _weights[k] : 1.0;
    private bool Collapsed(int k) => k >= 0 && k < _collapsed.Length && _collapsed[k];
    private bool Shown(int k) => _focusedTrack >= 0 ? k == _focusedTrack : !Collapsed(k);

    /// <summary>Pixel x-offset + width of a track's column, or null if it isn't currently shown.</summary>
    private (double x0, double w)? ColumnLayout(int track)
    {
        var cw = ContentWidth;
        if (cw <= 0 || track < 0 || track >= _trackCount || !Shown(track)) return null;
        if (_focusedTrack >= 0) return (0.0, cw); // single focused column fills the content area
        double total = 0;
        for (var k = 0; k < _trackCount; k++) if (Shown(k)) total += Weight(k);
        if (total <= 0) return null;
        double x = 0;
        for (var k = 0; k < track; k++) if (Shown(k)) x += cw * Weight(k) / total;
        return (x, cw * Weight(track) / total);
    }

    // The (left,right) track indices of the column divider near x, or (-1,-1) if none / not resizable.
    private (int left, int right) DividerTracksAt(double x)
    {
        if (_focusedTrack >= 0 || ContentWidth <= 0 || _total <= 0) return (-1, -1);
        var prev = -1;
        for (var k = 0; k < _trackCount; k++)
        {
            if (!Shown(k)) continue;
            if (prev >= 0 && ColumnLayout(k) is { } L && Math.Abs(x - L.x0) <= 4) return (prev, k);
            prev = k;
        }
        return (-1, -1);
    }

    /// <summary>Transcript bubble body font size (px).</summary>
    public void SetFontSize(double px)
    {
        px = Math.Clamp(px, 8, 28);
        if (Math.Abs(px - _fontSize) < 0.01) return;
        _fontSize = px;
        InvalidateVisual();
    }

    /// <summary>Manual-log markers: a horizontal line across the columns + a tag on the left edge that
    /// shows the log text on hover (mirrors the video timeline's markers).</summary>
    // Day markers: chapter dividers from the source folders. Full-width, always labelled — the
    // counterpart of the vertical dividers on the video timeline.
    private double[] _dayMarkers = Array.Empty<double>();
    private string[] _dayTexts = Array.Empty<string>();
    private string[] _dayColors = Array.Empty<string>();

    public void SetDayMarkers(double[] times, string[] texts, string[] colors)
    {
        _dayMarkers = times ?? Array.Empty<double>();
        _dayTexts = texts ?? Array.Empty<string>();
        _dayColors = colors ?? Array.Empty<string>();
        InvalidateVisual();
    }

    public void SetMarkers(double[] times, string[] texts, string[] colors)
    {
        _markers = times ?? Array.Empty<double>();
        _markerTexts = texts ?? Array.Empty<string>();
        _markerColors = colors ?? Array.Empty<string>();
        _hoveredMarker = -1; // the set changed → drop any stale hover (next pointer move re-establishes it)
        InvalidateVisual();
    }

    private IBrush MarkerBrush(int i)
    {
        var hex = (i >= 0 && i < _markerColors.Length && !string.IsNullOrEmpty(_markerColors[i])) ? _markerColors[i] : "#FFCB5C";
        if (_markerBrushCache.TryGetValue(hex, out var b)) return b;
        IBrush brush;
        try { brush = new ImmutableSolidColorBrush(Color.Parse(hex)); } catch { brush = new ImmutableSolidColorBrush(Color.Parse("#FFCB5C")); }
        _markerBrushCache[hex] = brush;
        return brush;
    }

    // The marker whose left-edge tag is under (x,y), or -1. The tag sits in the left ~14px at the marker's y.
    private int MarkerAtPoint(double x, double y)
    {
        if (x > 16 || _total <= 0) return -1;
        for (var i = 0; i < _markers.Length; i++)
        {
            var my = TimeToY(_markers[i]);
            if (my < HeaderHeight - 2 || my > Bounds.Height) continue;
            if (Math.Abs(my - y) <= 7) return i;
        }
        return -1;
    }

    private IPen MarkerPen(int i)
    {
        var hex = (i >= 0 && i < _markerColors.Length && !string.IsNullOrEmpty(_markerColors[i])) ? _markerColors[i] : "#FFCB5C";
        if (!_markerPenCache.TryGetValue(hex, out var pen))
        {
            // semi-transparent so bubbles stay readable beneath the line
            var c = Color.Parse(hex);
            pen = new ImmutablePen(new ImmutableSolidColorBrush(new Color(0xAA, c.R, c.G, c.B)), 1.5);
            _markerPenCache[hex] = pen;
        }
        return pen;
    }

    public void SetTrackSegments(int trackIdx, IReadOnlyList<TranscriptSegment> segments)
    {
        if (trackIdx < 0 || trackIdx >= _byTrack.Length) return;
        _byTrack[trackIdx] = new List<TranscriptSegment>(segments);
        InvalidateVisual();
    }

    /// <summary>The bubble at or before <paramref name="time"/> on a track (for the copy-last-bubble hotkey).</summary>
    public TranscriptSegment? LastBubbleAt(int track, double time)
    {
        if (track < 0 || track >= _byTrack.Length) return null;
        var col = _byTrack[track];
        if (col == null || col.Count == 0) return null;
        TranscriptSegment? best = null;
        foreach (var s in col)
        {
            if (s.Start <= time + 0.05) best = s;
            else break;
        }
        return best ?? col[0];
    }

    /// <summary>While Ctrl is held: outline the bubble Ctrl+1/2/3 would copy for each of the first three
    /// tracks at <paramref name="time"/>. Cheap no-op when the target set is unchanged.</summary>
    public void HighlightCopyTargets(double time)
    {
        var n = Math.Min(3, _trackCount);
        var fresh = new HashSet<TranscriptSegment>();
        for (var k = 0; k < n; k++)
        {
            var s = LastBubbleAt(k, time);
            if (s != null) fresh.Add(s);
        }
        if (fresh.SetEquals(_copyTargets)) return;
        _copyTargets.Clear();
        foreach (var s in fresh) _copyTargets.Add(s);
        InvalidateVisual();
    }

    /// <summary>Clear the Ctrl-copy-target outlines (Ctrl released).</summary>
    public void ClearCopyTargets()
    {
        if (_copyTargets.Count == 0) return;
        _copyTargets.Clear();
        InvalidateVisual();
    }

    public void SetTotal(double total)
    {
        if (Math.Abs(total - _total) < 0.001) return;
        _total = Math.Max(0, total);
        if (!_fitted && _total > 0 && Bounds.Height > HeaderHeight)
        {
            _pps = (Bounds.Height - HeaderHeight) / _total;
            _scroll = 0;
            _fitted = true;
            ViewportChanged?.Invoke(_pps, _scroll);
        }
        InvalidateVisual();
    }

    public void SetPlayhead(double seconds)
    {
        if (_scrubbing || _draggingDivider || _draggingMarker >= 0) return; // don't let the 60 Hz push fight a live gesture
        if (Math.Abs(seconds - _playhead) < 0.0005) return;
        _playhead = seconds;
        CenterPlayheadIfLocked();
        InvalidateVisual();
    }

    private bool _lockToCenter;
    /// <summary>When true, the transcript scrolls so the playhead stays in the middle of the view.</summary>
    public bool LockToCenter
    {
        get => _lockToCenter;
        set { _lockToCenter = value; CenterPlayheadIfLocked(); InvalidateVisual(); }
    }

    private void CenterPlayheadIfLocked()
    {
        if (!_lockToCenter || Bounds.Height <= HeaderHeight || _pps <= 0 || _total <= 0) return;
        var viewSec = (Bounds.Height - HeaderHeight) / _pps;
        _scroll = _playhead - viewSec / 2;
        ClampScroll();
        ViewportChanged?.Invoke(_pps, _scroll);
    }

    public void SetViewport(double pps, double scroll)
    {
        if (Math.Abs(pps - _pps) < 0.0001 && Math.Abs(scroll - _scroll) < 0.0001) return;
        _pps = Math.Max(0.0001, pps);
        _scroll = scroll;
        _fitted = true;
        ClampScroll();
        InvalidateVisual();
    }

    public double PixelsPerSecond => _pps;
    public double ScrollSeconds => _scroll;

    /// <summary>Scroll so the given time is centred vertically (does not fire ViewportChanged).</summary>
    public void CenterOn(double t)
    {
        if (Bounds.Height <= HeaderHeight || _pps <= 0 || _total <= 0) return;
        _scroll = t - (Bounds.Height - HeaderHeight) / _pps / 2;
        _fitted = true;
        ClampScroll();
        InvalidateVisual();
    }

    // ---- coordinates ----------------------------------------------------

    private double TimeToY(double t) => HeaderHeight + (t - _scroll) * _pps;
    private double YToTime(double y) => _scroll + (y - HeaderHeight) / _pps;

    private int ColumnAt(double x)
    {
        var cw = ContentWidth;
        if (_trackCount == 0 || cw <= 0 || x < 0 || x >= cw) return -1;
        for (var k = 0; k < _trackCount; k++)
            if (ColumnLayout(k) is { } L && x >= L.x0 && x < L.x0 + L.w) return k;
        return -1;
    }

    private int SegmentAt(int track, double y)
    {
        if (track < 0 || track >= _byTrack.Length) return -1;
        if (track < _hidden.Length && _hidden[track]) return -1; // hidden column → clicks scrub instead
        // Hit-test the RENDERED bubble rects (inflated to MinReadableHeight) so hovering/clicking the visible
        // bubble registers even when it's drawn taller than its time-span. Falls back to the time-span test
        // for any bubble that wasn't in the last draw (shouldn't happen for an on-screen bubble).
        var col = _byTrack[track];
        for (var i = 0; i < col.Count; i++)
            if (_drawnBubbles.TryGetValue((track, i), out var r) && y >= r.Top && y < r.Bottom) return i;
        var t = YToTime(y);
        for (var i = 0; i < col.Count; i++)
        {
            var s = col[i];
            if (t >= s.Start && t < s.End) return i;
            if (s.Start > t) break;
        }
        return -1;
    }

    // Minimum height for a bubble that shows text: roughly one line of body text + padding. Font-relative
    // so it tracks the Settings bubble font size. Below this, a segment renders as a cheap text-less sliver.
    private double MinReadableHeight() => _fontSize * 1.45 + 9;

    private Rect BubbleRect(int track, int segIdx)
    {
        if (ColumnLayout(track) is not { } L) return default;
        var s = _byTrack[track][segIdx];
        var y0 = TimeToY(s.Start);
        var y1 = TimeToY(s.End);
        var naturalH = Math.Max(2, y1 - y0 - 2);
        var bubbleH = Math.Max(MinReadableHeight(), naturalH);
        return new Rect(L.x0 + 3, y0 + 1, Math.Max(4, L.w - 6), bubbleH);
    }

    private bool IsOverCopyButton(int track, int segIdx, Point pos)
    {
        // Use the copy button's ACTUAL drawn rect (only populated for bubbles that rendered a button), so the
        // hit zone matches exactly what the user sees — no phantom hits on slivers, no dead zones on inflated bubbles.
        return _drawnCopyButtons.TryGetValue((track, segIdx), out var br) && br.Contains(pos);
    }

    private void ClampScroll()
    {
        if (Bounds.Height <= HeaderHeight) return;
        var viewSec = (Bounds.Height - HeaderHeight) / _pps;
        if (_total <= viewSec) _scroll = 0;
        else _scroll = Math.Clamp(_scroll, 0, _total - viewSec);
    }

    // ---- input ----------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Bounds.Height <= HeaderHeight || _total <= 0) return;
        var y = e.GetPosition(this).Y;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            var tUnder = YToTime(y);
            var factor = e.Delta.Y > 0 ? 1.18 : 1 / 1.18;
            var availH = Bounds.Height - HeaderHeight;
            var minPps = availH / _total;
            _pps = Math.Clamp(_pps * factor, Math.Min(minPps, 500), 600);
            _scroll = tUnder - (y - HeaderHeight) / _pps;
        }
        else
        {
            var viewSec = (Bounds.Height - HeaderHeight) / _pps;
            _scroll += (e.Delta.Y > 0 ? -1 : 1) * viewSec * 0.12;
        }
        ClampScroll();
        e.Handled = true;
        InvalidateVisual();
        ViewportChanged?.Invoke(_pps, _scroll);
        ViewportUserChanged?.Invoke();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        var pos = pt.Position;

        // Right-click → record what's under the cursor so the ContextMenu handler can find it.
        if (pt.Properties.IsRightButtonPressed)
        {
            var track = ColumnAt(pos.X);
            _rightClickTrack = track;
            _rightClickSeg = track >= 0 && pos.Y >= HeaderHeight ? SegmentAt(track, pos.Y) : -1;
            // Don't mark handled — let Avalonia open the ContextMenu we attached.
            return;
        }

        if (!pt.Properties.IsLeftButtonPressed) return;

        // Grab a marker's left-edge tag to DRAG it (retimes its log entry) — mirrors the video timeline.
        var mi = MarkerAtPoint(pos.X, pos.Y);
        if (mi >= 0)
        {
            _draggingMarker = mi;
            _markerDidMove = false;
            _markerDragStartY = pos.Y;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Drag a column divider (grabbed in the lane body, not the header) to re-scale two adjacent tracks.
        var (dl, dr) = pos.Y >= HeaderHeight ? DividerTracksAt(pos.X) : (-1, -1);
        if (dl >= 0 && dr >= 0 && ColumnLayout(dl) is { } ll && ColumnLayout(dr) is { } rl)
        {
            _draggingDivider = true;
            _divLeft = dl; _divRight = dr;
            _divStartX = pos.X;
            _divLeftW0 = ll.w; _divRightW0 = rl.w;
            _divCombinedWeight0 = Weight(dl) + Weight(dr);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (pos.Y < HeaderHeight)
        {
            // Header click toggles focus on that column.
            var idx = ColumnAt(pos.X);
            if (idx >= 0) ToggleFocus(idx);
            e.Handled = true;
            return;
        }

        var laneIdx = ColumnAt(pos.X);
        if (laneIdx >= 0)
        {
            var segIdx = SegmentAt(laneIdx, pos.Y);
            if (segIdx >= 0)
            {
                // Click on the per-bubble copy button → fire Copy without seeking.
                if (IsOverCopyButton(laneIdx, segIdx, pos))
                {
                    SegmentCopyRequested?.Invoke(laneIdx, _byTrack[laneIdx][segIdx]);
                    e.Handled = true;
                    return;
                }
                var t = _byTrack[laneIdx][segIdx].Start;
                _playhead = t;
                SeekRequested?.Invoke(t);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // Click on empty space → scrub the playhead.
        _scrubbing = true;
        e.Pointer.Capture(this);
        ScrubToY(pos.Y, force: true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_draggingMarker >= 0)
        {
            if (Math.Abs(pos.Y - _markerDragStartY) > MarkerDragThreshold) _markerDidMove = true;
            if (_markerDidMove && _draggingMarker < _markers.Length)
            {
                _markers[_draggingMarker] = Math.Clamp(YToTime(pos.Y), 0, _total);
                InvalidateVisual();
            }
            return;
        }

        if (_draggingDivider)
        {
            var dx = pos.X - _divStartX;
            var combinedW = _divLeftW0 + _divRightW0;
            var lo = MinColWidth;
            var hi = combinedW - MinColWidth;
            // If the pair is too narrow to honour both minimums, split evenly (Math.Clamp throws if min>max).
            var newLeftW = hi <= lo ? combinedW * 0.5 : Math.Clamp(_divLeftW0 + dx, lo, hi);
            var newRightW = combinedW - newLeftW;
            // Convert pixel widths back to weights (preserving the pair's combined weight).
            var wPerPx = _divCombinedWeight0 / Math.Max(1.0, combinedW);
            if (_divLeft < _weights.Length) _weights[_divLeft] = Math.Max(MinWeight, newLeftW * wPerPx);
            if (_divRight < _weights.Length) _weights[_divRight] = Math.Max(MinWeight, newRightW * wPerPx);
            InvalidateVisual();
            return;
        }

        if (_scrubbing) { ScrubToY(pos.Y, force: false); return; }

        // Marker tag hover (left edge) → tooltip with the log text.
        var mhit = MarkerAtPoint(pos.X, pos.Y);
        if (mhit != _hoveredMarker) { _hoveredMarker = mhit; InvalidateVisual(); }

        // Cursor feedback when hovering a draggable divider (lane body only, matching the press gate).
        var (dl, _) = pos.Y >= HeaderHeight ? DividerTracksAt(pos.X) : (-1, -1);
        var overDiv = dl >= 0;
        if (overDiv != _hoverDivider)
        {
            _hoverDivider = overDiv;
            Cursor = new Cursor(overDiv ? StandardCursorType.SizeWestEast : StandardCursorType.Arrow);
        }

        var t = ColumnAt(pos.X);
        var s = t >= 0 && pos.Y >= HeaderHeight ? SegmentAt(t, pos.Y) : -1;
        var overCopy = s >= 0 && IsOverCopyButton(t, s, pos);
        if (t != _hoverTrack || s != _hoverSeg || overCopy != _hoverCopyBtn)
        {
            _hoverTrack = t;
            _hoverSeg = s;
            _hoverCopyBtn = overCopy;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_draggingMarker >= 0)
        {
            var mi = _draggingMarker;
            _draggingMarker = -1;
            e.Pointer.Capture(null);
            if (mi >= 0 && mi < _markers.Length)
            {
                if (_markerDidMove) MarkerMoved?.Invoke(mi, _markers[mi]); // retime the log entry
                else SeekRequested?.Invoke(_markers[mi]);                  // a plain tag click jumps there
            }
            return;
        }
        if (_draggingDivider)
        {
            _draggingDivider = false;
            _divLeft = _divRight = -1;
            e.Pointer.Capture(null);
            LayoutChanged?.Invoke(); // persist the new column widths
            return;
        }
        if (_scrubbing) ScrubToY(e.GetPosition(this).Y, force: true);
        _scrubbing = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_draggingMarker >= 0) { _draggingMarker = -1; InvalidateVisual(); }
        if (_draggingDivider) { _draggingDivider = false; _divLeft = _divRight = -1; LayoutChanged?.Invoke(); }
        _scrubbing = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverDivider) { _hoverDivider = false; Cursor = new Cursor(StandardCursorType.Arrow); }
        if (_hoverTrack >= 0 || _hoverSeg >= 0 || _hoverCopyBtn || _hoveredMarker >= 0)
        {
            _hoverTrack = -1; _hoverSeg = -1; _hoverCopyBtn = false; _hoveredMarker = -1;
            InvalidateVisual();
        }
    }

    private void ScrubToY(double y, bool force)
    {
        if (_total <= 0) return;
        var t = Math.Clamp(YToTime(y), 0, _total);
        _playhead = t;
        InvalidateVisual();
        var now = Environment.TickCount64;
        if (force || now - _lastSeekTick >= 25)
        {
            _lastSeekTick = now;
            SeekRequested?.Invoke(t);
        }
    }

    private void ToggleFocus(int idx)
    {
        _focusedTrack = _focusedTrack == idx ? -1 : idx;
        InvalidateVisual();
    }

    // ---- search ---------------------------------------------------------

    public void SetSearch(string? s)
    {
        var v = (s ?? "").Trim();
        if (v == _search) return;
        _search = v;
        InvalidateVisual();
    }

    // Null-safe: this runs inside Render for every visible bubble, and an exception there is fatal to the
    // whole app (Avalonia has no per-render recovery). A segment restored from a project file can have null
    // Text if the JSON carried a null, so never dereference it directly.
    private bool Matches(TranscriptSegment s)
        => _search.Length > 0 && s?.Text is { } t && t.Contains(_search, StringComparison.OrdinalIgnoreCase);

    public int SearchMatchCount()
    {
        if (_search.Length == 0) return 0;
        var n = 0;
        foreach (var col in _byTrack)
        {
            if (col == null) continue;
            foreach (var s in col) if (Matches(s)) n++;
        }
        return n;
    }

    // Scroll to the next/prev matching bubble relative to the current view centre (wraps around).
    public void ScrollToMatch(int dir)
    {
        if (_search.Length == 0 || _total <= 0 || Bounds.Height <= HeaderHeight) return;
        var matches = new List<double>();
        foreach (var col in _byTrack)
        {
            if (col == null) continue;
            foreach (var s in col) if (Matches(s)) matches.Add(s.Start);
        }
        if (matches.Count == 0) return;
        matches.Sort();

        var viewSec = (Bounds.Height - HeaderHeight) / _pps;
        var center = _scroll + viewSec / 2;
        double? target = null;
        if (dir >= 0)
        {
            foreach (var m in matches) if (m > center + 0.05) { target = m; break; }
            target ??= matches[0];
        }
        else
        {
            for (var i = matches.Count - 1; i >= 0; i--) if (matches[i] < center - 0.05) { target = matches[i]; break; }
            target ??= matches[^1];
        }
        _scroll = target.Value - viewSec / 2;
        ClampScroll();
        InvalidateVisual();
        ViewportChanged?.Invoke(_pps, _scroll);
    }

    // ---- render ---------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        var swRender = System.Diagnostics.Stopwatch.StartNew();
        try { RenderCore(ctx); }
        finally
        {
            if (swRender.ElapsedMilliseconds >= 5)
                FootageReviewer.App.Util.DiagnosticsLogger.NoteUiOp($"TranscriptTimelineControl.Render(pps={_pps:0.###})", Environment.TickCount64 - swRender.ElapsedMilliseconds, swRender.ElapsedMilliseconds);
        }
    }

    private void RenderCore(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        ctx.FillRectangle(_bg, new Rect(0, 0, w, h));

        if (_trackCount == 0 || _total <= 0)
        {
            var msg = _trackCount == 0
                ? "Load footage so the transcript can lay out one column per audio track."
                : "Waiting for media duration…";
            var ft = new FormattedText(msg, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _tf, 12, _headerText);
            ctx.DrawText(ft, new Point(16, 24));
            return;
        }

        if (!_fitted && _total > 0 && h > HeaderHeight)
        {
            _pps = (h - HeaderHeight) / _total;
            _scroll = 0;
            _fitted = true;
        }

        var cw = ContentWidth;
        using (ctx.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Aliased }))
        {
            DrawHeader(ctx, cw);
            DrawColumns(ctx, cw, h);
            DrawPinnedCopyTargets(ctx, cw, h); // Ctrl held + target scrolled off-screen → pin it at the top
            DrawDayMarkers(ctx, cw); // beneath the log markers, same as the video timeline
            DrawMarkers(ctx, cw);
            DrawTimeRuler(ctx, w, h);
        }
        // Antialiased + fractional so the playhead glides smoothly with the 60 Hz interpolation.
        DrawPlayhead(ctx, w);
        DrawMarkerHoverTip(ctx, w, h); // on top of everything
    }

    // Vertical time ruler down the right edge — tick marks + H:MM:SS labels at a zoom-appropriate step.
    private void DrawTimeRuler(DrawingContext ctx, double w, double h)
    {
        var x0 = w - RightRulerWidth;
        ctx.FillRectangle(_headerBg, new Rect(x0, HeaderHeight, RightRulerWidth, Math.Max(0, h - HeaderHeight)));
        ctx.DrawLine(_laneSep, new Point(Math.Round(x0) + 0.5, HeaderHeight), new Point(Math.Round(x0) + 0.5, h));
        if (_total <= 0 || _pps <= 0) return;

        var topT = _scroll;
        var botT = _scroll + (h - HeaderHeight) / _pps;
        var step = ChooseRulerStep();
        var first = Math.Ceiling(topT / step) * step;
        using var _ = ctx.PushClip(new Rect(x0, HeaderHeight, RightRulerWidth, Math.Max(0, h - HeaderHeight)));
        for (var t = first; t <= botT; t += step)
        {
            var y = Math.Round(TimeToY(t)) + 0.5;
            if (y < HeaderHeight || y > h) continue;
            ctx.DrawLine(_laneSep, new Point(x0, y), new Point(x0 + 5, y));
            var ft = new FormattedText(FmtRuler(t), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _tf, 10, _headerText);
            ctx.DrawText(ft, new Point(x0 + 7, y - ft.Height / 2));
        }
    }

    // Pick a tick spacing (seconds) so labels are ~comfortably spaced for the current zoom.
    private double ChooseRulerStep()
    {
        double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        var minPx = 36.0; // min pixels between labels
        foreach (var s in steps) if (s * _pps >= minPx) return s;
        return steps[^1];
    }

    private static string FmtRuler(double s)
    {
        if (s < 0) s = 0;
        var ts = TimeSpan.FromSeconds(s);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    // While Ctrl is held, a copy-target bubble that's scrolled off-screen (e.g. the playhead is well past the
    // last thing said) is pinned as a compact readable copy at the top of its track column, with the same green
    // outline + an arrow showing where the real bubble is, so the user can read what Ctrl+1/2/3 would copy.
    private void DrawPinnedCopyTargets(DrawingContext ctx, double w, double h)
    {
        if (_copyTargets.Count == 0 || _total <= 0 || _pps <= 0) return;
        using var _ = ctx.PushClip(new Rect(0, HeaderHeight, w, Math.Max(0, h - HeaderHeight)));
        for (var k = 0; k < _trackCount; k++)
        {
            if (ColumnLayout(k) is not { } L) continue;
            if (k < _hidden.Length && _hidden[k]) continue;
            // Each copy-target segment belongs to exactly one track's list — find this column's, if any.
            TranscriptSegment? target = null;
            foreach (var s in _byTrack[k]) if (_copyTargets.Contains(s)) { target = s; break; }
            if (target == null) continue;

            // Skip if the bubble's first line is already on-screen (readable in place → no pin needed).
            var ty = TimeToY(target.Start);
            if (ty >= HeaderHeight && ty <= h - (_fontSize + 8)) continue;

            var innerW = Math.Max(4, L.w - 6);
            // Show the WHOLE log text (no arrow, no truncation), capped so it can't fill the whole column.
            var maxH = Math.Max(_fontSize * 3, (h - HeaderHeight) * 0.6);
            var ft = new FormattedText(string.IsNullOrEmpty(target.Text) ? "(…)" : target.Text,
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _tf, _fontSize, _segText)
            { MaxTextWidth = Math.Max(10, innerW - 12), MaxTextHeight = maxH };
            var pinH = ft.Height + 10;
            var rect = new Rect(L.x0 + 3, HeaderHeight + 3, innerW, pinH);
            ctx.DrawRectangle(_segActive, _copyTargetBorder, rect, 3, 3);
            using (ctx.PushClip(rect))
                ctx.DrawText(ft, new Point(rect.X + 6, rect.Y + 5));
        }
    }

    private void DrawDayMarkers(DrawingContext ctx, double w)
    {
        if (_total <= 0 || _dayMarkers.Length == 0) return;
        var topT = _scroll;
        var botT = _scroll + (Bounds.Height - HeaderHeight) / _pps;
        for (var i = 0; i < _dayMarkers.Length; i++)
        {
            var t = _dayMarkers[i];
            if (t < topT || t > botT) continue;
            var y = Math.Round(TimeToY(t)) + 0.5;
            if (y < HeaderHeight || y > Bounds.Height) continue;

            var hex = i < _dayColors.Length && !string.IsNullOrEmpty(_dayColors[i]) ? _dayColors[i] : "#FF5A5A";
            IBrush brush; try { brush = new SolidColorBrush(Color.Parse(hex)); } catch { brush = Brushes.Red; }

            ctx.DrawLine(new Pen(brush, 2), new Point(0, y), new Point(w, y));

            var label = i < _dayTexts.Length ? _dayTexts[i] : null;
            if (string.IsNullOrWhiteSpace(label)) continue;
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       _tf, 11, _dayLabelText);
            const double padX = 5, padY = 2;
            var rect = new Rect(2, y + 2, ft.Width + padX * 2, ft.Height + padY * 2);
            if (rect.Bottom <= Bounds.Height)
            {
                ctx.FillRectangle(brush, rect, 3);
                ctx.DrawText(ft, new Point(rect.X + padX, rect.Y + padY));
            }
        }
    }

    private void DrawMarkers(DrawingContext ctx, double w)
    {
        if (_total <= 0 || _markers.Length == 0) return;
        var topT = _scroll;
        var botT = _scroll + (Bounds.Height - HeaderHeight) / _pps;
        for (var i = 0; i < _markers.Length; i++)
        {
            var t = _markers[i];
            if (t < topT || t > botT) continue;
            var y = Math.Round(TimeToY(t)) + 0.5;
            if (y < HeaderHeight || y > Bounds.Height) continue;
            ctx.DrawLine(MarkerPen(i), new Point(0, y), new Point(w, y));
            // Left-edge tag (Premiere-style), solid colour, a small right-pointing pennant at the marker.
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, y - 5), true);
                g.LineTo(new Point(9, y - 5));
                g.LineTo(new Point(13, y));
                g.LineTo(new Point(9, y + 5));
                g.LineTo(new Point(0, y + 5));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(MarkerBrush(i), null, geo);
        }
    }

    // Tooltip with the log text for the hovered marker tag (mirrors the video timeline's hover tip).
    private void DrawMarkerHoverTip(DrawingContext ctx, double w, double h)
    {
        if (_hoveredMarker < 0 || _hoveredMarker >= _markers.Length) return;
        var text = _hoveredMarker < _markerTexts.Length ? _markerTexts[_hoveredMarker] : "";
        if (string.IsNullOrWhiteSpace(text)) text = "(empty entry)";
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _tf, 12, _hoverText) { MaxTextWidth = 340 };
        var pad = 8.0;
        var boxW = ft.Width + pad * 2;
        var boxH = ft.Height + pad * 2;
        var my = TimeToY(_markers[_hoveredMarker]);
        var bx = 16.0;
        if (bx + boxW > w - 4) bx = Math.Max(4, w - 4 - boxW);
        var by = Math.Clamp(my - boxH / 2, HeaderHeight + 2, Math.Max(HeaderHeight + 2, h - boxH - 2));
        var rect = new Rect(bx, by, boxW, boxH);
        ctx.DrawRectangle(_hoverBg, _hoverBorder, rect, 4, 4);
        ctx.DrawText(ft, new Point(bx + pad, by + pad));
    }

    private void DrawHeader(DrawingContext ctx, double w)
    {
        ctx.FillRectangle(_headerBg, new Rect(0, 0, Bounds.Width, HeaderHeight)); // full-width header bar
        var first = true;
        for (var i = 0; i < _trackCount; i++)
        {
            if (ColumnLayout(i) is not { } L) continue;
            var brush = i == _focusedTrack ? _headerTextFocused : _headerText;
            var ft = new FormattedText(_titles[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _tfHead, 13, brush) { MaxTextWidth = Math.Max(10, L.w - 6) };
            var x = L.x0 + (L.w - ft.Width) / 2;
            ctx.DrawText(ft, new Point(Math.Round(x), 4));
            if (!first)
            {
                var sx = Math.Round(L.x0) + 0.5;
                ctx.DrawLine(_laneSep, new Point(sx, 0), new Point(sx, HeaderHeight));
            }
            first = false;
        }
    }

    private void DrawColumns(DrawingContext ctx, double w, double h)
    {
        var topT = _scroll;
        var botT = _scroll + (h - HeaderHeight) / _pps;

        // Clip the entire column-drawing pass so bubbles can never overpaint the header band.
        using var _ = ctx.PushClip(new Rect(0, HeaderHeight, w, Math.Max(0, h - HeaderHeight)));

        _drawnBubbles.Clear();      // rebuilt below so hover/click hit-test the rendered geometry
        _drawnCopyButtons.Clear();
        var firstCol = true;
        for (var k = 0; k < _trackCount; k++)
        {
            if (ColumnLayout(k) is not { } L) continue;
            var x0 = L.x0;
            var colW = L.w;
            var bg = (k & 1) == 0 ? _laneBg : _laneBgAlt;
            ctx.FillRectangle(bg, new Rect(x0, HeaderHeight, colW, h - HeaderHeight));

            var col = _byTrack[k];
            var hidden = k < _hidden.Length && _hidden[k];
            // Empty / hidden column → a hint instead of a blank box that looks like a bug.
            if (hidden)
            {
                var hint = new FormattedText("(bubbles hidden)", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _tf, 11, _emptyHint) { MaxTextWidth = Math.Max(10, colW - 16) };
                ctx.DrawText(hint, new Point(x0 + 8, HeaderHeight + 8));
            }
            else if (col.Count == 0)
            {
                var hint = new FormattedText("(no speech detected)", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _tf, 11, _emptyHint) { MaxTextWidth = Math.Max(10, colW - 16) };
                ctx.DrawText(hint, new Point(x0 + 8, HeaderHeight + 8));
            }
            var innerW = Math.Max(4, colW - 6);
            var minReadable = MinReadableHeight(); // hoisted out of the loop
            // Skip the (potentially huge) prefix of segments that end before the viewport — binary-search to
            // the first one that could be visible, then break once we pass the bottom. This is what makes a
            // far zoom-out smooth when a track has thousands of segments (was an O(n)-per-frame scan).
            var startIdx = hidden ? col.Count : FirstVisibleSegment(col, topT);
            for (var i = startIdx; i < col.Count; i++)
            {
                var s = col[i];
                if (s.Start > botT) break;   // sorted by Start → everything after is below the viewport
                if (s.End < topT) continue;  // ends above the viewport
                var y0 = TimeToY(s.Start);
                var nextTopY = (i + 1 < col.Count) ? TimeToY(col[i + 1].Start) : h;
                var available = nextTopY - y0 - 2;
                var isMatch = Matches(s);
                var isTarget = _copyTargets.Count > 0 && _copyTargets.Contains(s);
                // Cull dense sub-pixel slivers when zoomed far out (they'd just merge into a solid blob), but
                // never hide the active bubble, a search match, or a Ctrl-copy target.
                if (available < 2.5 && !s.IsActive && !isMatch && !isTarget) continue;
                var y1 = TimeToY(s.End);
                var naturalH = Math.Max(2, y1 - y0 - 2);
                // Perf: only when there's room below for a readable line do we measure the full text (the
                // expensive part). When densely packed / zoomed out, segments render as cheap text-less slivers.
                var showText = available >= minReadable;
                double bubbleH;
                FormattedText? bodyFt = null;
                if (showText)
                {
                    var textW = Math.Max(10, innerW - 8 - (CopyButtonSize + 4));
                    bodyFt = new FormattedText(s.Text ?? "", CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, _tf, _fontSize, _segText) { MaxTextWidth = textW };
                    var neededH = bodyFt.Height + 10; // body + padding (no timestamp line anymore)
                    // A single-line bubble fits its content exactly (no longer forced up to a 36px floor or
                    // inflated to its time-span — that's what made short utterances look too tall). Multi-
                    // line bubbles still grow to fill their duration so longer speech reads as a taller block.
                    var singleLine = bodyFt.Height < _fontSize * 2.0;
                    var desired = singleLine ? neededH : Math.Max(naturalH, neededH);
                    bubbleH = available >= desired ? desired : available; // grow to fit, capped at the gap
                }
                else
                {
                    bubbleH = naturalH; // sliver
                }

                var rect = new Rect(x0 + 3, y0 + 1, innerW, bubbleH);
                _drawnBubbles[(k, i)] = rect; // for hover/click hit-testing the visible bubble

                IBrush fill = (i & 1) == 0 ? _seg : _segAlt;
                if (s.IsActive) fill = _segActive;
                else if (isMatch) fill = _segMatch;
                else if (k == _hoverTrack && i == _hoverSeg) fill = _segHover;
                ctx.DrawRectangle(fill, isMatch ? _segMatchBorder : _segBorder, rect, 3, 3);
                // Ctrl-held: outline the bubble this track's Ctrl+1/2/3 would copy.
                if (isTarget)
                    ctx.DrawRectangle(null, _copyTargetBorder, rect, 3, 3);

                if (showText && bodyFt != null && rect.Height > 16 && rect.Width > 28)
                {
                    using (ctx.PushClip(rect))
                    {
                        // No timestamp inside the bubble (the time is on the right-edge ruler) — the body
                        // text starts at the top so short bubbles have room for more words.
                        var bodyTop = rect.Y + 3;
                        if (rect.Bottom - bodyTop > 4)
                        {
                            bodyFt.MaxTextHeight = Math.Max(1, rect.Bottom - bodyTop - 2);
                            ctx.DrawText(bodyFt, new Point(rect.X + 4, bodyTop));
                        }

                        // Copy-to-clipboard button in the top-right corner of every bubble.
                        if (rect.Width > 32)
                        {
                            var btnRect = CopyButtonRect(rect);
                            _drawnCopyButtons[(k, i)] = btnRect; // exact copy-button hit zone
                            var hover = k == _hoverTrack && i == _hoverSeg && _hoverCopyBtn;
                            ctx.DrawRectangle(hover ? _copyBtnHover : _copyBtnBg, _copyBtnBorder, btnRect, 3, 3);
                            var glyph = new FormattedText("⧉", CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, _tf, 11, _copyBtnText);
                            ctx.DrawText(glyph, new Point(
                                btnRect.X + (btnRect.Width - glyph.Width) / 2,
                                btnRect.Y + (btnRect.Height - glyph.Height) / 2));
                        }
                    }
                }
            }
            if (!firstCol)
            {
                var sx = Math.Round(x0) + 0.5;
                ctx.DrawLine(_laneSep, new Point(sx, HeaderHeight), new Point(sx, h));
            }
            firstCol = false;
        }
    }

    // First segment index that could be visible at/after topT. Segments are time-sorted by Start, so we
    // binary-search the first Start >= topT and step back one (the prior segment may be long enough to overlap).
    private static int FirstVisibleSegment(List<TranscriptSegment> col, double topT)
    {
        int lo = 0, hi = col.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (col[mid].Start < topT) lo = mid + 1; else hi = mid;
        }
        return Math.Max(0, lo - 1);
    }

    private static Rect CopyButtonRect(Rect bubble)
        => new(bubble.Right - CopyButtonSize - 4, bubble.Top + 3, CopyButtonSize, CopyButtonSize);

    private void DrawPlayhead(DrawingContext ctx, double w)
    {
        if (_total <= 0) return;
        var y = TimeToY(_playhead);
        if (y < HeaderHeight || y > Bounds.Height) return;
        // Fractional y (no pixel-snapping) so the line moves sub-pixel smoothly with the interpolated playhead.
        ctx.DrawLine(_play, new Point(0, y), new Point(w, y));
    }
}
