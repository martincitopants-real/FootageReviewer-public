using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace FootageReviewer.App.Controls;

/// <summary>A clip on the timeline: where it starts on the global timeline and how long it runs.</summary>
public sealed class TimelineClip
{
    public string Name = "";
    public string? SourcePath;
    public double Start;     // absolute seconds on the continuous EDL timeline
    public double Duration;  // seconds

    // DaVinci-style filmstrip: multiple frames captured along the clip. ThumbnailTimes[i] is the
    // CLIP-RELATIVE second the bitmap at Thumbnails[i] was sampled from. Render maps each visible
    // tile to the nearest-by-time frame, so panning along the clip shows what's there at that point.
    public Bitmap[]? Thumbnails;
    public double[]? ThumbnailTimes;
}

/// <summary>
/// Premiere-style multi-lane timeline: a video lane over N audio lanes, each clip drawn as a
/// distinct rectangle, a thin draggable playhead, and Alt+scroll zoom. Virtualized — it only ever
/// draws the visible time window, never a surface sized to total×pps. Waveforms (volume-scaled)
/// plug into the audio lanes in a later pass via SetWaveform.
/// </summary>
public sealed class TimelineControl : Control
{
    public const double RulerHeight = 22;
    public const double VideoLaneHeight = 72;
    public const double AudioLaneHeight = 64;

    // viewport
    private double _total;
    private double _playhead;
    private double _pps = 10;      // pixels per second (zoom)
    private double _scroll;        // seconds at x = 0
    private bool _fitted;
    private bool _dragging;
    private long _lastSeekTick;

    private TimelineClip[] _clips = Array.Empty<TimelineClip>();
    private int _audioCount;

    // per-track display state (drives dimming + waveform scaling)
    private double[] _vol = Array.Empty<double>();
    private bool[] _muted = Array.Empty<bool>();
    private bool[] _solo = Array.Empty<bool>();

    // waveform peaks per (clip, track). inner array can be null (not yet extracted).
    private float[][][] _waveforms = Array.Empty<float[][]>();

    // auto-level chunks per track: (start, end, gain). Drives the volume-reflected waveform + the
    // chunk boundary markers when auto-level is on.
    private (double Start, double End, double Gain)[][] _autoChunks = Array.Empty<(double, double, double)[]>();
    private bool[] _autoLevelOn = Array.Empty<bool>();   // per-track enable (each track toggles independently)
    private const double WaveBucketsPerSec = 32.0;
    private readonly IPen _chunkTick = P("#9082E0A0"); // light green — distinct from coloured markers

    private bool AutoOn(int track) => track >= 0 && track < _autoLevelOn.Length && _autoLevelOn[track];

    public void SetAutoLevelChunks(int track, (double Start, double End, double Gain)[] chunks, bool enabled)
    {
        if (_autoChunks.Length != _audioCount) _autoChunks = new (double, double, double)[Math.Max(0, _audioCount)][];
        if (_autoLevelOn.Length != _audioCount) _autoLevelOn = new bool[Math.Max(0, _audioCount)];
        if (track >= 0 && track < _autoChunks.Length) _autoChunks[track] = chunks;
        if (track >= 0 && track < _autoLevelOn.Length) _autoLevelOn[track] = enabled;
        InvalidateVisual();
    }

    private double AutoGainAt(int track, double absT)
    {
        if (!AutoOn(track) || track < 0 || track >= _autoChunks.Length) return 1.0;
        var ch = _autoChunks[track];
        if (ch == null || ch.Length == 0) return 1.0;
        // chunks are time-sorted; small linear scan with early-out is fine for the visible window.
        foreach (var c in ch)
        {
            if (absT >= c.Start && absT < c.End) return c.Gain;
            if (c.Start > absT) break;
        }
        return 1.0; // gaps render at the un-boosted level
    }

    // manual-log markers (canonical video seconds), drawn Premiere-style on the ruler.
    private double[] _markers = Array.Empty<double>();
    private string[] _markerTexts = Array.Empty<string>();
    private string[] _markerColors = Array.Empty<string>();
    /// <summary>Raised with the marker index when a marker is right-clicked (for the colour menu).</summary>
    public event Action<int>? MarkerRightClicked;
    /// <summary>Right-click on a day divider's line or label. Index into the day-marker arrays.</summary>
    public event Action<int>? DayMarkerRightClicked;
    /// <summary>Raised with the marker index when a marker tag is left-clicked (no drag).</summary>
    public event Action<int>? MarkerClicked;
    /// <summary>Raised with (index, newTimeSeconds) when a marker tag is dragged to a new time.</summary>
    public event Action<int, double>? MarkerMoved;
    private readonly Dictionary<string, IBrush> _markerBrushCache = new();
    private int _draggingMarker = -1;   // marker tag being dragged on the ruler
    private bool _markerDidMove;         // distinguishes a drag from a plain click

    // Reorder: a RIGHT-CLICK-selected clip can be left-dragged to a new boundary.
    private int _draggingClip = -1;     // clip being dragged (-1 = none)
    private bool _clipDidMove;          // distinguishes a reorder drag from a plain click (→ seek)
    private int _clipDropBoundary = -1; // live drop target (0..clipCount) while dragging
    private double _clipDragStartX;     // press x — a real drag needs > a few px of travel (else it's a click)
    private const double ClipDragThreshold = 4;
    /// <summary>Raised with (fromClipIndex, toBoundaryIndex) when a selected clip is dragged to a new slot.</summary>
    public event Action<int, int>? ClipMoved;

    // skip-preview guide lines (playhead ± skip seconds)
    private double _skipPreviewSec;
    private bool _skipPreviewVisible;
    public void SetSkipPreview(double sec, bool visible)
    {
        if (Math.Abs(sec - _skipPreviewSec) < 0.001 && visible == _skipPreviewVisible) return;
        _skipPreviewSec = sec;
        _skipPreviewVisible = visible;
        InvalidateVisual();
    }

    // temporary "M" bookmark — a single transient marker, drawn distinct from the log markers.
    private double? _tempMarker;
    public void SetTempMarker(double? t)
    {
        _tempMarker = t;
        InvalidateVisual();
    }

    // hover state — rendered in Render() rather than via a Popup. A Popup is a top-level OS window
    // that intercepts the cursor (causing flashing) and races with PointerExited (causing the
    // "from below" miss). Drawing into our own DrawingContext avoids both.
    private int _hoveredMarker = -1;

    /// <summary>Raised with an absolute-seconds target when the user clicks/drags the timeline.</summary>
    public event Action<double>? SeekRequested;
    /// <summary>Raised on every zoom/pan; lets MainWindow mirror onto the transcript timeline.</summary>
    public event Action<double, double>? ViewportChanged;
    /// <summary>Raised only on a genuine user zoom/pan (mouse wheel), not programmatic viewport changes.</summary>
    public event Action? ViewportUserChanged;
    /// <summary>Raised when the set of selected clip indices changes.</summary>
    public event Action<int[]>? SelectionChanged;

    // selection state
    private readonly HashSet<int> _selectedClips = new();
    private Point? _marqueeStart;       // shift+drag selection
    private Point? _marqueeCurrent;
    public IReadOnlyCollection<int> SelectedClips => _selectedClips;
    public void ClearSelection()
    {
        if (_selectedClips.Count == 0) return;
        _selectedClips.Clear();
        SelectionChanged?.Invoke(Array.Empty<int>());
        InvalidateVisual();
    }

    public double PixelsPerSecond => _pps;
    public double ScrollSeconds => _scroll;

    /// <summary>Apply an externally-driven viewport (e.g. when "Lock zoom" is on).</summary>
    public void SetViewport(double pps, double scroll)
    {
        if (Math.Abs(pps - _pps) < 0.0001 && Math.Abs(scroll - _scroll) < 0.0001) return;
        _pps = Math.Max(0.0001, pps);
        _scroll = scroll;
        _fitted = true;
        ClampScroll();
        InvalidateVisual();
    }

    /// <summary>Scroll so the given time is centred horizontally (does not fire ViewportChanged).</summary>
    public void CenterOn(double t)
    {
        if (Bounds.Width <= 0 || _pps <= 0 || _total <= 0) return;
        _scroll = t - Bounds.Width / _pps / 2;
        _fitted = true;
        ClampScroll();
        InvalidateVisual();
    }

    // ---- cached drawing resources (no per-frame allocation) ----
    private static ImmutableSolidColorBrush B(string c) => new(Color.Parse(c));
    private static IPen P(string c, double w = 1) => new ImmutablePen(B(c), w);

    private readonly IBrush _bg = B("#0E0E13");
    private readonly IBrush _rulerBg = B("#16161D");
    private readonly IBrush _laneBg = B("#12121A");
    private readonly IBrush _laneBgAlt = B("#0F0F16");
    private readonly IBrush _vClip = B("#33476E");
    private readonly IBrush _vClipAlt = B("#2A3A5C");
    private readonly IBrush _aClip = B("#274050");
    private readonly IBrush _aClipAlt = B("#203440");
    private readonly IBrush _wave = B("#AAB4E0FF");
    private readonly IBrush _dim = B("#88000000");
    private readonly IBrush _clipText = B("#BFD2F2");
    private readonly IBrush _tickText = B("#8A8A99");
    private readonly IBrush _dayLabelText = B("#12121A"); // dark on the coloured day chip
    private readonly IPen _tick = P("#22FFFFFF");
    private readonly IPen _clipBorder = P("#66000000");
    private readonly IPen _laneSep = P("#33000000");
    private readonly IPen _boundary = P("#553A82FF");
    private readonly IPen _play = P("#FFE0405A");
    private readonly IBrush _markerFill = B("#FFCB5C");
    private readonly IPen _markerLine = P("#55FFCB5C");
    private readonly IPen _skipLine = P("#553A82FF");
    private readonly IBrush _tempFill = B("#FF3DD6E0");   // temp "M" marker — cyan, distinct from log markers
    private readonly IPen _tempLine = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#CC3DD6E0")), 1.5,
                                                       new ImmutableDashStyle(new double[] { 4, 3 }, 0));
    private readonly IBrush _hoverBg = B("#F01B1B25");
    private readonly IPen _hoverBorder = P("#FFCB5C");
    private readonly IBrush _hoverText = B("#FFFFFF");
    private readonly IPen _selBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFE0405A")), 2);
    private readonly IBrush _marqueeFill = B("#33FFCB5C");
    private readonly IPen _marqueeBorder = P("#FFCB5C", 1);
    private readonly IPen _dropIndicator = P("#FF8CE6A0", 2.5); // where a dragged clip will land
    private readonly Typeface _tf = new("Inter");
    private readonly Dictionary<string, FormattedText> _ftCache = new();

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetClips(TimelineClip[] clips, double total, bool preserveView = false)
    {
        _clips = clips ?? Array.Empty<TimelineClip>();
        // The ffprobe-summed total is 0 for av:// sources or failed probes; don't let it wipe the
        // mpv-derived duration (set via SetTotal) — that zeroed _total made CenterOn no-op and pinned
        // the restored scroll to the start. Duration is owned by SetTotal; clear it explicitly there.
        if (total > 0) _total = total;
        // A fresh load auto-fits the whole timeline to the window; a SEAMLESS clip edit (reorder / insert /
        // delete) keeps the user's current zoom + scroll so the view doesn't jump to the start / zoom out.
        if (!preserveView) _fitted = false;
        else ClampScroll();
        _thumbPending = new bool[_clips.Length]; // reset extraction spinners for the new clip set
        ResizeWaveformsGrid();
        UpdateSpinTimer();
        InvalidateVisual();
    }

    // Read-back of a clip's decoded assets, so a seamless reorder can CARRY them to the clip's new index
    // instead of clearing + re-extracting (which made the filmstrip/waveforms flicker on every move).
    public Bitmap[]? ThumbnailsOf(int clipIdx) => clipIdx >= 0 && clipIdx < _clips.Length ? _clips[clipIdx].Thumbnails : null;
    public double[]? ThumbnailTimesOf(int clipIdx) => clipIdx >= 0 && clipIdx < _clips.Length ? _clips[clipIdx].ThumbnailTimes : null;
    public float[][]? WaveformsOf(int clipIdx) => clipIdx >= 0 && clipIdx < _waveforms.Length ? _waveforms[clipIdx] : null;

    /// <summary>Drop all displayed filmstrips (for "Regenerate thumbnails" — they refill as extraction runs).</summary>
    public void ClearThumbnails()
    {
        foreach (var c in _clips) { c.Thumbnails = null; c.ThumbnailTimes = null; }
        InvalidateVisual();
    }

    /// <summary>Drop all displayed waveforms (for "Regenerate waveforms" — they refill as extraction runs).</summary>
    public void ClearWaveforms()
    {
        for (var i = 0; i < _waveforms.Length; i++) _waveforms[i] = null!; // grid re-allocates null cells
        InvalidateVisual();
    }

    public void SetClipThumbnails(int clipIdx, Bitmap[] bmps, double[] times)
    {
        if (clipIdx < 0 || clipIdx >= _clips.Length) return;
        if (bmps.Length != times.Length) return;
        _clips[clipIdx].Thumbnails = bmps;
        _clips[clipIdx].ThumbnailTimes = times;
        SetThumbPending(clipIdx, false); // its filmstrip arrived → drop the loading spinner
        InvalidateVisual();
    }

    public void SetClipWaveform(int clipIdx, int trackIdx, float[] peaks)
    {
        if (clipIdx < 0 || clipIdx >= _clips.Length) return;
        if (_audioCount == 0) return;
        if (trackIdx < 0 || trackIdx >= _audioCount) return;
        ResizeWaveformsGrid();
        _waveforms[clipIdx][trackIdx] = peaks;
        SetWavePending(clipIdx, trackIdx, false); // this track's waveform arrived → drop the spinner
        InvalidateVisual();
    }

    // ---- extraction loading spinners (waveforms + thumbnails) ----
    private bool[] _thumbPending = Array.Empty<bool>();
    private bool[][] _wavePending = Array.Empty<bool[]>();
    private Avalonia.Threading.DispatcherTimer? _spinTimer;
    private int _spinFrame;
    private static readonly string[] SpinFrames = { "◐", "◓", "◑", "◒" };
    private readonly IBrush _spinBg = B("#CC0E0E13");
    private readonly IBrush _spinFg = B("#FFD27A");

    /// <summary>Mark a clip's filmstrip as still extracting (shows a corner spinner) or done.</summary>
    public void SetThumbPending(int clipIdx, bool pending)
    {
        if (clipIdx < 0 || clipIdx >= _clips.Length) return;
        if (_thumbPending.Length != _clips.Length) _thumbPending = new bool[_clips.Length];
        if (_thumbPending[clipIdx] == pending) return;
        _thumbPending[clipIdx] = pending;
        UpdateSpinTimer();
        InvalidateVisual();
    }

    /// <summary>Mark a clip/track's waveform as still extracting (shows a corner spinner) or done.</summary>
    public void SetWavePending(int clipIdx, int trackIdx, bool pending)
    {
        if (clipIdx < 0 || clipIdx >= _clips.Length || trackIdx < 0) return;
        ResizeWaveformsGrid();
        if (clipIdx >= _wavePending.Length || trackIdx >= _wavePending[clipIdx].Length) return;
        if (_wavePending[clipIdx][trackIdx] == pending) return;
        _wavePending[clipIdx][trackIdx] = pending;
        UpdateSpinTimer();
        InvalidateVisual();
    }

    private bool AnyPending()
    {
        foreach (var b in _thumbPending) if (b) return true;
        foreach (var row in _wavePending) if (row != null) foreach (var b in row) if (b) return true;
        return false;
    }

    // Run a light repaint timer ONLY while something is extracting (so the spinner animates even when paused),
    // and stop it when nothing's pending so an idle timeline costs nothing.
    private void UpdateSpinTimer()
    {
        var need = AnyPending();
        if (need && _spinTimer == null)
        {
            _spinTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            _spinTimer.Tick += (_, _) => { _spinFrame++; InvalidateVisual(); };
            _spinTimer.Start();
        }
        else if (!need && _spinTimer != null) { _spinTimer.Stop(); _spinTimer = null; }
    }

    private void DrawSpinner(DrawingContext ctx, Rect clip)
    {
        if (clip.Width < 18 || clip.Height < 14) return; // too small to bother
        const double sz = 13;
        var bx = clip.Right - sz - 3;
        var by = clip.Y + 3;
        var badge = new Rect(bx, by, sz, sz);
        ctx.DrawRectangle(_spinBg, null, badge, 3, 3);
        var ft = Ft(SpinFrames[((_spinFrame % 4) + 4) % 4], _spinFg);
        ctx.DrawText(ft, new Point(bx + (sz - ft.Width) / 2, by + (sz - ft.Height) / 2 - 1));
    }

    private void ResizeWaveformsGrid()
    {
        if (_waveforms.Length != _clips.Length)
            _waveforms = new float[_clips.Length][][];
        if (_wavePending.Length != _clips.Length)
            _wavePending = new bool[_clips.Length][];
        for (var i = 0; i < _clips.Length; i++)
        {
            if (_waveforms[i] == null || _waveforms[i].Length != _audioCount)
            {
                var prev = _waveforms[i];
                var nw = new float[Math.Max(0, _audioCount)][];
                if (prev != null) for (var k = 0; k < Math.Min(prev.Length, nw.Length); k++) nw[k] = prev[k];
                _waveforms[i] = nw;
            }
            if (_wavePending[i] == null || _wavePending[i].Length != _audioCount)
                _wavePending[i] = new bool[Math.Max(0, _audioCount)];
        }
    }

    public void SetTotal(double total)
    {
        if (Math.Abs(total - _total) < 0.001) return;
        _total = Math.Max(0, total);
        InvalidateVisual();
    }

    public void SetTracks(int audioCount)
    {
        _audioCount = audioCount;
        if (_vol.Length != audioCount)
        {
            _vol = new double[audioCount];
            _muted = new bool[audioCount];
            _solo = new bool[audioCount];
            for (var i = 0; i < audioCount; i++) _vol[i] = 1.0;
        }
        // Reset auto-level state so a previous project's per-track flags/chunks don't bleed through.
        _autoLevelOn = new bool[Math.Max(0, audioCount)];
        _autoChunks = new (double, double, double)[Math.Max(0, audioCount)][];
        ResizeWaveformsGrid();
        Height = RulerHeight + VideoLaneHeight + audioCount * AudioLaneHeight;
        InvalidateVisual();
    }

    public void SetTrackState(int i, double vol, bool muted, bool solo)
    {
        if (i < 0 || i >= _audioCount) return;
        _vol[i] = vol; _muted[i] = muted; _solo[i] = solo;
        InvalidateVisual();
    }

    // Day markers: chapter dividers from the source folders. Deliberately unlike log markers — full
    // height, always labelled, no hover needed — so a day boundary reads as structure rather than as
    // another note on the timeline.
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

    public void SetMarkers(double[] markers, string[]? texts = null, string[]? colors = null)
    {
        _markers = markers ?? Array.Empty<double>();
        _markerTexts = texts ?? Array.Empty<string>();
        _markerColors = colors ?? Array.Empty<string>();
        if (_hoveredMarker >= _markers.Length) _hoveredMarker = -1;
        InvalidateVisual();
    }

    /// <summary>Index of the day divider within a few pixels of <paramref name="x"/>, or -1.</summary>
    private int DayMarkerAtX(double x)
    {
        for (var i = 0; i < _dayMarkers.Length; i++)
            if (Math.Abs(TimeToX(_dayMarkers[i]) - x) <= 6) return i;
        return -1;
    }

    private IBrush MarkerBrush(int i)
    {
        var hex = i < _markerColors.Length && !string.IsNullOrEmpty(_markerColors[i]) ? _markerColors[i] : "#FFCB5C";
        if (_markerBrushCache.TryGetValue(hex, out var b)) return b;
        IBrush brush;
        try { brush = new ImmutableSolidColorBrush(Color.Parse(hex)); } catch { brush = _markerFill; }
        _markerBrushCache[hex] = brush;
        return brush;
    }

    private readonly Dictionary<string, IPen> _markerLinePenCache = new();
    private IPen MarkerLinePen(int i)
    {
        var hex = i < _markerColors.Length && !string.IsNullOrEmpty(_markerColors[i]) ? _markerColors[i] : "#FFCB5C";
        if (_markerLinePenCache.TryGetValue(hex, out var p)) return p;
        IPen pen;
        try { var c = Color.Parse(hex); pen = new ImmutablePen(new ImmutableSolidColorBrush(new Color(0x55, c.R, c.G, c.B)), 1); }
        catch { pen = _markerLine; }
        _markerLinePenCache[hex] = pen;
        return pen;
    }

    // index of the marker whose ruler tag is under x (or -1)
    private int MarkerAtX(double x)
    {
        for (var i = 0; i < _markers.Length; i++)
            if (Math.Abs(TimeToX(_markers[i]) - x) <= 7) return i;
        return -1;
    }

    public void SetPlayhead(double seconds)
    {
        if (_dragging || _draggingMarker >= 0 || _draggingClip >= 0) return; // don't scroll the ruler mid-drag
        if (Math.Abs(seconds - _playhead) < 0.0005) return;
        _playhead = seconds;
        CenterPlayheadIfLocked();
        KeepPlayheadOnScreen();
        InvalidateVisual();
    }

    private bool _lockToCenter;
    /// <summary>When true, the timeline scrolls so the playhead stays in the middle of the view.</summary>
    public bool LockToCenter
    {
        get => _lockToCenter;
        set { _lockToCenter = value; CenterPlayheadIfLocked(); InvalidateVisual(); }
    }

    // ---- range selection (the "copy a section to Premiere" tool) ----
    private bool _selActive;
    private double _selStart, _selEnd;
    private readonly IBrush _selFill = B("#3348C8FF");
    private readonly IPen _selEdge = P("#FF48C8FF", 2);

    /// <summary>Raised when the wheel is turned while the selection tool is open (+1 = grow, -1 = shrink).</summary>
    public event Action<int>? SelectionResize;
    /// <summary>Timeline time under the cursor, reported while the selection tool is open so it can follow the mouse.</summary>
    public event Action<double>? SelectionAnchorMoved;

    /// <summary>True while the section-select band is showing.</summary>
    public bool SelectionActive => _selActive;

    /// <summary>Show/most the selection band. Pass null to hide it.</summary>
    public void SetSelection(double? start, double? end)
    {
        var active = start.HasValue && end.HasValue && end.Value > start.Value;
        if (!active && !_selActive) return;
        _selActive = active;
        if (active) { _selStart = start!.Value; _selEnd = end!.Value; }
        InvalidateVisual();
    }

    private bool _keepVisible;
    /// <summary>Looser alternative to <see cref="LockToCenter"/>: the view is free to sit anywhere, but as soon
    /// as the playhead leaves it the timeline jumps so the playhead is back in the middle. Ignored while
    /// centre-lock is on (that already keeps it pinned).</summary>
    public bool KeepPlayheadVisible
    {
        get => _keepVisible;
        set { _keepVisible = value; KeepPlayheadOnScreen(); InvalidateVisual(); }
    }

    private void CenterPlayheadIfLocked()
    {
        if (!_lockToCenter || Bounds.Width <= 0 || _pps <= 0 || _total <= 0) return;
        var viewSec = Bounds.Width / _pps;
        _scroll = _playhead - viewSec / 2;
        ClampScroll();
        ViewportChanged?.Invoke(_pps, _scroll);
    }

    // Re-centre only when the playhead has actually gone off-screen (a small margin in from each edge so it
    // doesn't sit right on the boundary). Cheap no-op while it's comfortably in view.
    private void KeepPlayheadOnScreen()
    {
        if (!_keepVisible || _lockToCenter || _dragging || Bounds.Width <= 0 || _pps <= 0 || _total <= 0) return;
        var viewSec = Bounds.Width / _pps;
        var margin = viewSec * 0.02;
        if (_playhead >= _scroll + margin && _playhead <= _scroll + viewSec - margin) return; // still visible
        _scroll = _playhead - viewSec / 2;
        ClampScroll();
        ViewportChanged?.Invoke(_pps, _scroll);
    }

    private double TimeToX(double t) => (t - _scroll) * _pps;
    private double XToTime(double x) => _scroll + x / _pps;

    // ---- input ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Bounds.Width <= 0 || _total <= 0) return;
        var x = e.GetPosition(this).X;

        // While the selection tool is open the wheel resizes the selection — EXCEPT with Alt held, which
        // still zooms the timeline as usual.
        if (_selActive && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            SelectionResize?.Invoke(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Zoom around the cursor. Use only the SIGN of Delta.Y (magnitude is unreliable).
            var tUnder = XToTime(x);
            var factor = e.Delta.Y > 0 ? 1.18 : 1 / 1.18;
            var minPps = Bounds.Width / _total / 1.0; // fit-to-window is the zoomed-out limit
            _pps = Math.Clamp(_pps * factor, Math.Min(minPps, 500), 600);
            _scroll = tUnder - x / _pps;
        }
        else
        {
            var viewSec = Bounds.Width / _pps;
            _scroll += (e.Delta.Y > 0 ? -1 : 1) * viewSec * 0.12;
        }

        ClampScroll();
        e.Handled = true;
        InvalidateVisual();
        ViewportChanged?.Invoke(_pps, _scroll);
        ViewportUserChanged?.Invoke();
    }

    private void ClampScroll()
    {
        var viewSec = Bounds.Width / _pps;
        if (_total <= viewSec) _scroll = 0;
        else _scroll = Math.Clamp(_scroll, 0, _total - viewSec);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this);
        var pos = p.Position;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Right-click on a marker tag (top ruler band) → raise for the colour menu.
        if (p.Properties.IsRightButtonPressed && pos.Y <= RulerHeight + 2)
        {
            var mi = MarkerAtX(pos.X);
            if (mi >= 0) { MarkerRightClicked?.Invoke(mi); e.Handled = true; return; }
        }

        // Day dividers span the full height, so they're right-clickable anywhere down the line — not
        // just in the ruler, where a log marker would already have claimed the hit above.
        if (p.Properties.IsRightButtonPressed)
        {
            var di = DayMarkerAtX(pos.X);
            if (di >= 0) { DayMarkerRightClicked?.Invoke(di); e.Handled = true; return; }
        }

        // Right-click: select the clip under the cursor (Shift extends selection).
        if (p.Properties.IsRightButtonPressed)
        {
            var clipIdx = ClipAt(pos);
            if (clipIdx >= 0)
            {
                if (!shift) _selectedClips.Clear();
                if (!_selectedClips.Add(clipIdx)) _selectedClips.Remove(clipIdx); // toggle on shift-click
                SelectionChanged?.Invoke(_selectedClips.ToArray());
                InvalidateVisual();
            }
            else if (!shift)
            {
                ClearSelection();
            }
            e.Handled = true;
            return;
        }

        if (!p.Properties.IsLeftButtonPressed) return;

        // Left-press on a marker tag (top ruler band) → begin a potential drag. A press with no movement
        // fires MarkerClicked on release (centre its log entry); a drag retimes the marker.
        if (pos.Y <= RulerHeight + 2)
        {
            var mi = MarkerAtX(pos.X);
            if (mi >= 0)
            {
                _draggingMarker = mi;
                _markerDidMove = false;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        // Left-press on an ALREADY-SELECTED clip (video lane, no shift) → begin a potential reorder drag.
        // A press with no movement falls through to a normal seek on release. Selection is via right-click.
        if (!shift && pos.Y >= RulerHeight && pos.Y < RulerHeight + VideoLaneHeight)
        {
            var ci = ClipAt(pos);
            if (ci >= 0 && _selectedClips.Contains(ci))
            {
                _draggingClip = ci;
                _clipDidMove = false;
                _clipDropBoundary = ci;
                _clipDragStartX = pos.X;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        // Shift+left-drag: marquee selection across the video lane.
        if (shift && pos.Y >= RulerHeight && pos.Y < RulerHeight + VideoLaneHeight)
        {
            _marqueeStart = pos;
            _marqueeCurrent = pos;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        SeekToX(pos.X, force: true);
        e.Handled = true;
    }

    private int ClipAt(Point pos)
    {
        if (pos.Y < RulerHeight || pos.Y >= RulerHeight + VideoLaneHeight) return -1;
        var t = XToTime(pos.X);
        for (var i = 0; i < _clips.Length; i++)
        {
            var c = _clips[i];
            if (t >= c.Start && t < c.Start + c.Duration) return i;
        }
        return -1;
    }

    /// <summary>The clip-boundary index (0..clipCount) nearest to screen-x — i.e. where a dropped/moved clip
    /// should be inserted. Boundaries sit at each clip's start, plus one after the last clip.</summary>
    public int BoundaryIndexAtX(double x)
    {
        if (_clips.Length == 0) return 0;
        var t = XToTime(x);
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < _clips.Length; i++)
        {
            var d = Math.Abs(_clips[i].Start - t);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        var last = _clips[^1];
        if (Math.Abs(last.Start + last.Duration - t) < bestDist) best = _clips.Length;
        return best;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        // The section-select band tracks the cursor (not the playhead) while the tool is open.
        if (_selActive && _total > 0 && _pps > 0)
            SelectionAnchorMoved?.Invoke(Math.Clamp(XToTime(pos.X), 0, _total));
        if (_draggingMarker >= 0)
        {
            if (_draggingMarker < _markers.Length && _total > 0)
            {
                _markers[_draggingMarker] = Math.Clamp(XToTime(pos.X), 0, _total);
                _markerDidMove = true;
                InvalidateVisual();
            }
            return;
        }
        if (_draggingClip >= 0)
        {
            // Only treat it as a drag once the pointer travels past a small threshold — sub-pixel jitter
            // during a plain click must still fall through to a seek on release.
            if (!_clipDidMove && Math.Abs(pos.X - _clipDragStartX) > ClipDragThreshold) _clipDidMove = true;
            if (_clipDidMove)
            {
                _clipDropBoundary = BoundaryIndexAtX(pos.X);
                InvalidateVisual();
            }
            return;
        }
        if (_marqueeStart != null)
        {
            _marqueeCurrent = pos;
            InvalidateVisual();
            return;
        }
        if (_dragging)
        {
            SeekToX(pos.X, force: false);
            return;
        }

        // Marker hover hit zone: anywhere within the ruler band that overlaps the marker tag's
        // visible region (about 14 px tall, plus a couple of px of slack for approach from below).
        var hit = -1;
        if (pos.Y >= 0 && pos.Y <= RulerHeight + 2)
        {
            for (var i = 0; i < _markers.Length; i++)
            {
                var mx = TimeToX(_markers[i]);
                if (Math.Abs(mx - pos.X) <= 7) { hit = i; break; }
            }
        }
        if (hit != _hoveredMarker)
        {
            _hoveredMarker = hit;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoveredMarker >= 0) { _hoveredMarker = -1; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_draggingMarker >= 0)
        {
            var mi = _draggingMarker;
            _draggingMarker = -1;
            e.Pointer.Capture(null);
            if (mi >= 0 && mi < _markers.Length) // ignore a stale index if the marker set changed mid-gesture
            {
                if (_markerDidMove) MarkerMoved?.Invoke(mi, _markers[mi]);
                else MarkerClicked?.Invoke(mi); // a press with no drag = click
            }
            return;
        }
        if (_draggingClip >= 0)
        {
            var from = _draggingClip;
            var to = _clipDropBoundary;
            var moved = _clipDidMove;
            _draggingClip = -1; _clipDropBoundary = -1;
            e.Pointer.Capture(null);
            InvalidateVisual();
            if (moved && from >= 0) ClipMoved?.Invoke(from, to);
            else SeekToX(e.GetPosition(this).X, force: true); // no drag → a plain click seeks
            return;
        }
        if (_marqueeStart is { } a && _marqueeCurrent is { } b)
        {
            // Commit marquee: select every clip whose time range overlaps [tA, tB].
            double xMin = Math.Min(a.X, b.X), xMax = Math.Max(a.X, b.X);
            var tMin = XToTime(xMin);
            var tMax = XToTime(xMax);
            var add = !e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (!add) _selectedClips.Clear();
            for (var i = 0; i < _clips.Length; i++)
            {
                var c = _clips[i];
                if (c.Start + c.Duration > tMin && c.Start < tMax) _selectedClips.Add(i);
            }
            SelectionChanged?.Invoke(_selectedClips.ToArray());
            _marqueeStart = _marqueeCurrent = null;
            InvalidateVisual();
            e.Pointer.Capture(null);
            return;
        }
        if (_dragging) SeekToX(e.GetPosition(this).X, force: true);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragging = false;
        _draggingMarker = -1;
        if (_draggingClip >= 0) { _draggingClip = -1; _clipDropBoundary = -1; InvalidateVisual(); }
        if (_marqueeStart != null) { _marqueeStart = _marqueeCurrent = null; InvalidateVisual(); }
    }

    private void SeekToX(double x, bool force)
    {
        if (_total <= 0) return;
        var t = Math.Clamp(XToTime(x), 0, _total);
        _playhead = t;
        InvalidateVisual();
        var now = Environment.TickCount64;
        if (force || now - _lastSeekTick >= 25) // throttle live drag seeks to ~40Hz
        {
            _lastSeekTick = now;
            SeekRequested?.Invoke(t);
        }
    }

    // ---- render ----

    public override void Render(DrawingContext ctx)
    {
        var swRender = System.Diagnostics.Stopwatch.StartNew();
        try { RenderCore(ctx); }
        finally
        {
            if (swRender.ElapsedMilliseconds >= 5)
                FootageReviewer.App.Util.DiagnosticsLogger.NoteUiOp($"TimelineControl.Render(pps={_pps:0.###})", Environment.TickCount64 - swRender.ElapsedMilliseconds, swRender.ElapsedMilliseconds);
        }
    }

    private void RenderCore(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        ctx.FillRectangle(_bg, new Rect(0, 0, w, h));
        if (_total <= 0) return;

        if (!_fitted && w > 0)
        {
            _pps = w / _total;
            _scroll = 0;
            _fitted = true;
        }

        var leftT = _scroll;
        var rightT = _scroll + w / _pps;

        // Crisp, pixel-snapped chrome (the ruler ticks + labels) is drawn Aliased.
        using (ctx.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Aliased }))
        {
            DrawRuler(ctx, w, leftT, rightT);
        }
        // Both lanes are drawn ANTIALIASED (NOT inside an Aliased scope — a nested PushRenderOptions
        // doesn't reliably override the outer EdgeMode in Avalonia 11.3, which left the waveform envelope
        // aliased and made its sub-pixel scroll under centre-lock snap/"dance"). Drawing video + audio
        // lanes together keeps their rounded clip frames visually consistent.
        DrawVideoLane(ctx, w, leftT, rightT);
        DrawAudioLanes(ctx, w, leftT, rightT);
        // Back to Aliased for the thin vertical reference lines drawn over everything.
        using (ctx.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Aliased }))
        {
            DrawClipBoundaries(ctx, h, leftT, rightT);
            DrawDayMarkers(ctx, h, leftT, rightT); // under the log markers: a log pin stays hittable on top
            DrawMarkers(ctx, h, leftT, rightT);
            DrawSkipPreview(ctx, h);
            DrawTempMarker(ctx, h);
            DrawClipDropIndicator(ctx, h);
            DrawMarkerHoverTip(ctx, w);
            DrawMarquee(ctx);
        }
        DrawSelection(ctx, h);
        // The playhead is drawn ANTIALIASED at a fractional x so it glides smoothly with the 60 Hz
        // interpolation (a pixel-snapped playhead would step a whole pixel at a time).
        DrawPlayhead(ctx, h);
    }

    // While dragging a selected clip, show where it will land (a bright vertical line at the boundary).
    private void DrawClipDropIndicator(DrawingContext ctx, double h)
    {
        if (_draggingClip < 0 || !_clipDidMove || _clips.Length == 0) return;
        var b = _clipDropBoundary;
        double bt;
        if (b <= 0) bt = _clips[0].Start;
        else if (b >= _clips.Length) { var last = _clips[^1]; bt = last.Start + last.Duration; }
        else bt = _clips[b].Start;
        var x = Math.Round(TimeToX(bt)) + 0.5;
        ctx.DrawLine(_dropIndicator, new Point(x, RulerHeight), new Point(x, h));
    }

    private static readonly int[] SkipSigns = { -1, 1 };

    private void DrawSkipPreview(DrawingContext ctx, double h)
    {
        if (!_skipPreviewVisible || _skipPreviewSec <= 0 || _total <= 0) return;
        foreach (var sign in SkipSigns) // (was a stackalloc; kept off the stack on principle)
        {
            var t = _playhead + sign * _skipPreviewSec;
            if (t < 0 || t > _total) continue;
            var x = Math.Round(TimeToX(t)) + 0.5;
            ctx.DrawLine(_skipLine, new Point(x, RulerHeight), new Point(x, h));
        }
    }

    private void DrawTempMarker(DrawingContext ctx, double h)
    {
        if (_tempMarker is not double t || _total <= 0) return;
        if (t < _scroll || t > _scroll + Bounds.Width / _pps) return;
        var x = Math.Round(TimeToX(t)) + 0.5;
        ctx.DrawLine(_tempLine, new Point(x, RulerHeight), new Point(x, h));
        // a small pennant flag at the top so it reads as a transient bookmark
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x, RulerHeight - 13), true);
            g.LineTo(new Point(x + 11, RulerHeight - 10));
            g.LineTo(new Point(x, RulerHeight - 7));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(_tempFill, null, geo);
    }

    private void DrawMarquee(DrawingContext ctx)
    {
        if (_marqueeStart is not { } a || _marqueeCurrent is not { } b) return;
        var rect = new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                            Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        ctx.DrawRectangle(_marqueeFill, _marqueeBorder, rect);
    }

    private void DrawMarkerHoverTip(DrawingContext ctx, double w)
    {
        if (_hoveredMarker < 0 || _hoveredMarker >= _markers.Length) return;
        var text = _hoveredMarker < _markerTexts.Length ? _markerTexts[_hoveredMarker] : "";
        if (string.IsNullOrWhiteSpace(text)) text = "(empty entry)";

        // Build the formatted text (cached). Wrap to ~340 px so a long entry stays readable.
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _tf, 12, _hoverText) { MaxTextWidth = 340 };

        var pad = 8.0;
        var boxW = ft.Width + pad * 2;
        var boxH = ft.Height + pad * 2;

        var mx = TimeToX(_markers[_hoveredMarker]);
        // Prefer the box to the right of the marker; if it would clip, flip left.
        var bx = mx + 10;
        if (bx + boxW > w - 4) bx = Math.Max(4, mx - 10 - boxW);
        // Sit just below the ruler so the marker stays visible above the tip.
        var by = RulerHeight + 4;

        var rect = new Rect(bx, by, boxW, boxH);
        ctx.DrawRectangle(_hoverBg, _hoverBorder, rect, 4, 4);
        ctx.DrawText(ft, new Point(bx + pad, by + pad));
    }

    private void DrawDayMarkers(DrawingContext ctx, double h, double leftT, double rightT)
    {
        for (var i = 0; i < _dayMarkers.Length; i++)
        {
            var t = _dayMarkers[i];
            if (t < leftT || t > rightT) continue;

            var hex = i < _dayColors.Length && !string.IsNullOrEmpty(_dayColors[i]) ? _dayColors[i] : "#FF5A5A";
            IBrush brush; try { brush = new SolidColorBrush(Color.Parse(hex)); } catch { brush = Brushes.Red; }

            var x = Math.Round(TimeToX(t)) + 0.5;
            // Full height, including through the ruler — that is what separates it from a log marker.
            ctx.DrawLine(new Pen(brush, 2), new Point(x, 0), new Point(x, h));

            var label = i < _dayTexts.Length ? _dayTexts[i] : null;
            if (string.IsNullOrWhiteSpace(label)) continue;

            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       _tf, 11, _dayLabelText);
            const double padX = 5, padY = 2;
            var boxW = ft.Width + padX * 2;
            // Keep the label on screen when its line is scrolled off to the left.
            var bx = Math.Max(0, Math.Min(x + 1, Bounds.Width - boxW));
            var rect = new Rect(bx, 0, boxW, ft.Height + padY * 2);
            ctx.FillRectangle(brush, rect, 3);
            ctx.DrawText(ft, new Point(rect.X + padX, rect.Y + padY));
        }
    }

    private void DrawMarkers(DrawingContext ctx, double h, double leftT, double rightT)
    {
        for (var i = 0; i < _markers.Length; i++)
        {
            var m = _markers[i];
            if (m < leftT || m > rightT) continue;
            var x = Math.Round(TimeToX(m)) + 0.5;
            var brush = MarkerBrush(i);
            ctx.DrawLine(MarkerLinePen(i), new Point(x, RulerHeight), new Point(x, h));
            // a downward "house" tag sitting at the bottom of the ruler (Premiere-style marker)
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(x - 5, RulerHeight - 12), true);
                g.LineTo(new Point(x + 5, RulerHeight - 12));
                g.LineTo(new Point(x + 5, RulerHeight - 4));
                g.LineTo(new Point(x, RulerHeight));
                g.LineTo(new Point(x - 5, RulerHeight - 4));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, geo);
        }
    }

    private void DrawRuler(DrawingContext ctx, double w, double leftT, double rightT)
    {
        ctx.FillRectangle(_rulerBg, new Rect(0, 0, w, RulerHeight));
        var step = ChooseTickStep();
        var first = Math.Ceiling(leftT / step) * step;
        for (var t = first; t <= rightT; t += step)
        {
            var x = Math.Round(TimeToX(t)) + 0.5;
            ctx.DrawLine(_tick, new Point(x, 0), new Point(x, Bounds.Height));
            ctx.DrawText(Ft(FmtTick(t), _tickText), new Point(x + 3, 3));
        }
    }

    private void DrawVideoLane(DrawingContext ctx, double w, double leftT, double rightT)
    {
        var top = RulerHeight;
        ctx.FillRectangle(_laneBg, new Rect(0, top, w, VideoLaneHeight));
        for (var i = 0; i < _clips.Length; i++)
        {
            var c = _clips[i];
            if (c.Start + c.Duration < leftT || c.Start > rightT) continue;
            var x0 = TimeToX(c.Start);
            var x1 = TimeToX(c.Start + c.Duration);
            var rect = new Rect(x0 + 1, top + 2, Math.Max(1, x1 - x0 - 2), VideoLaneHeight - 4);
            var border = _selectedClips.Contains(i) ? _selBorder : _clipBorder;
            ctx.DrawRectangle((i & 1) == 0 ? _vClip : _vClipAlt, border, rect, 3, 3);

            // DaVinci-style filmstrip: each tile shows the frame closest in TIME to its position
            // within the clip. Clip the loop to the viewport — at high zoom this rect can extend
            // hundreds of thousands of px and iterating off-screen tiles kills the CPU.
            if (c.Thumbnails is { Length: > 0 } bmps && c.ThumbnailTimes is { } times
                && rect.Width > 16 && rect.Height > 16 && c.Duration > 0)
            {
                var thumbH = rect.Height - 4;
                var firstBmp = bmps[0];
                var srcAspect = firstBmp.PixelSize.Width / (double)firstBmp.PixelSize.Height;
                var thumbW = thumbH * srcAspect;
                if (thumbW > 0)
                {
                    var startX = rect.X + 1;
                    var endX = rect.Right - 1;
                    var visX0 = Math.Max(startX, 0);
                    var visX1 = Math.Min(endX, w);
                    if (visX1 > visX0)
                    {
                        var tilesSkipped = Math.Floor((visX0 - startX) / thumbW);
                        var firstTile = startX + tilesSkipped * thumbW;
                        using (ctx.PushClip(new Rect(visX0, rect.Y + 2, visX1 - visX0, rect.Height - 4)))
                        {
                            for (var x = firstTile; x < visX1; x += thumbW)
                            {
                                var tCenter = (x + thumbW / 2 - rect.X) / rect.Width * c.Duration;
                                var bmp = NearestThumb(bmps, times, tCenter);
                                if (bmp == null) continue;
                                var src = new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
                                ctx.DrawImage(bmp, src, new Rect(x, rect.Y + 2, thumbW, thumbH));
                            }
                        }
                        ctx.DrawRectangle(null, _clipBorder, rect, 3, 3);
                    }
                }
            }

            if (rect.Width > 46)
                ctx.DrawText(Ft(c.Name, _clipText), new Point(rect.X + 6, rect.Y + 4));
            if (i < _thumbPending.Length && _thumbPending[i]) DrawSpinner(ctx, rect); // filmstrip still extracting
        }
    }

    private void DrawAudioLanes(DrawingContext ctx, double w, double leftT, double rightT)
    {
        var anySolo = Array.Exists(_solo, s => s);
        for (var k = 0; k < _audioCount; k++)
        {
            var top = RulerHeight + VideoLaneHeight + k * AudioLaneHeight;
            ctx.FillRectangle((k & 1) == 0 ? _laneBg : _laneBgAlt, new Rect(0, top, w, AudioLaneHeight));
            for (var i = 0; i < _clips.Length; i++)
            {
                var c = _clips[i];
                if (c.Start + c.Duration < leftT || c.Start > rightT) continue;
                var x0 = TimeToX(c.Start);
                var x1 = TimeToX(c.Start + c.Duration);
                var rect = new Rect(x0 + 1, top + 2, Math.Max(1, x1 - x0 - 2), AudioLaneHeight - 4);
                var aborder = _selectedClips.Contains(i) ? _selBorder : _clipBorder; // selection spans all lanes
                ctx.DrawRectangle((i & 1) == 0 ? _aClip : _aClipAlt, aborder, rect, 2, 2);

                // Waveform inside the clip rect, scaled by the fader AND (when on) the per-chunk
                // auto-level gain, so the picture reflects what you actually hear.
                if (_waveforms.Length > i && _waveforms[i] != null && _waveforms[i].Length > k)
                {
                    var peaks = _waveforms[i][k];
                    if (peaks != null && peaks.Length > 0)
                        DrawWaveform(ctx, peaks, rect, _vol[k], k, c.Start);
                }
                if (i < _wavePending.Length && _wavePending[i] != null && k < _wavePending[i].Length && _wavePending[i][k])
                    DrawSpinner(ctx, rect); // this track's waveform still generating
            }

            // Auto-level chunk boundary markers — subtle ticks at the top of the lane.
            if (AutoOn(k) && k < _autoChunks.Length && _autoChunks[k] is { } chunks)
            {
                // NO stackalloc in here: a stackalloc inside a loop allocates fresh stack on EVERY iteration and
                // only frees it when the method returns. Zoomed fully out on a 150-hour project this loop sees
                // ~100k chunks, which blew the 1.5 MB UI-thread stack (0xC00000FD in coreclr's stack probe).
                // Also dedupe by pixel: at that zoom thousands of ticks land on the same column.
                var lastBx = double.NaN;
                foreach (var ch in chunks)
                {
                    if (ch.End < leftT || ch.Start > rightT) continue;
                    for (var e = 0; e < 2; e++)
                    {
                        var bt = e == 0 ? ch.Start : ch.End;
                        if (bt < leftT || bt > rightT) continue;
                        var bx = Math.Round(TimeToX(bt)) + 0.5;
                        if (bx == lastBx) continue;
                        lastBx = bx;
                        ctx.DrawLine(_chunkTick, new Point(bx, top + 2), new Point(bx, top + 12));
                    }
                }
            }

            // Silenced (muted, or not soloed while something is soloed) → dim the lane.
            var silenced = _muted[k] || (anySolo && !_solo[k]);
            if (silenced)
                ctx.FillRectangle(_dim, new Rect(0, top, w, AudioLaneHeight));
            // +0.5 so the 1px separator stays crisp now that the audio lane renders antialiased.
            var sy = Math.Round(top) + 0.5;
            ctx.DrawLine(_laneSep, new Point(0, sy), new Point(w, sy));
        }
    }

    private void DrawWaveform(DrawingContext ctx, float[] peaks, Rect rect, double volScale, int track, double clipStart)
    {
        if (peaks.Length == 0 || rect.Width < 2 || rect.Height < 4) return;
        // Reflect the fader: louder (>100%) draws a taller waveform (clipped to the lane), quieter
        // draws smaller. 0 → invisible; a tiny floor keeps low-but-audible tracks visible.
        var baseScale = volScale <= 0 ? 0 : Math.Max(volScale, 0.12);
        if (baseScale <= 0) return;
        var laneH = rect.Height - 4;
        var bottomY = rect.Bottom - 2;

        // The waveform is a polyline whose VERTICES sit at fractional screen-x ANCHORED to a fixed
        // partition of the bucket array: window k = buckets [k*stride, (k+1)*stride). Because the windows
        // are fixed (independent of scroll) and only their x = rect.X + k*stride/spp shifts with the
        // sub-pixel scroll, the silhouette is a constant shape that simply TRANSLATES — no per-column
        // bucket churn, so it no longer shimmers/"dances" under centre-lock. (The old code put vertices at
        // whole-pixel screen-x and recomputed each column's bucket span from the moving scroll, so the
        // shape morphed in place rather than translating.) MAX-over-window keeps true peaks at zoom-out;
        // at zoom-in (stride 1) it's a smooth line between buckets.
        var spp = peaks.Length / Math.Max(1.0, rect.Width);    // buckets per pixel
        var stride = Math.Max(1, (int)Math.Round(spp));        // ~1 vertex per pixel at zoom-out
        var visLeft = Math.Max(0.0, rect.X);
        var visRight = Math.Min(Bounds.Width, rect.Right);
        if (visRight <= visLeft + 1) return;

        // Fractional screen-x of bucket b. Drifts smoothly sub-pixel with rect.X (= (clipStart-_scroll)*pps).
        double XOf(int b) => rect.X + b * rect.Width / peaks.Length;
        var kStart = (int)Math.Floor((visLeft - rect.X) * spp / stride) - 1;
        var kEnd = (int)Math.Ceiling((visRight - rect.X) * spp / stride) + 1;
        if (kStart < 0) kStart = 0;
        var kMax = (peaks.Length - 1) / stride;
        if (kEnd > kMax) kEnd = kMax;
        if (kEnd < kStart) return;

        // Forward cursor over this track's chunks (time-sorted) so the per-window gain lookup is O(1)
        // amortized rather than rescanning from the start each window.
        var chunks = (AutoOn(track) && track < _autoChunks.Length) ? _autoChunks[track] : null;
        var ci = 0;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(XOf(kStart * stride), bottomY), true);
            var lastTopY = bottomY;
            for (var k = kStart; k <= kEnd; k++)
            {
                var b0 = k * stride;
                var b1 = Math.Min(peaks.Length, b0 + stride);
                float peak = 0f;
                for (var s = b0; s < b1; s++) if (peaks[s] > peak) peak = peaks[s];

                var gain = 1.0;
                if (chunks is { Length: > 0 })
                {
                    var absT = clipStart + b0 / WaveBucketsPerSec;
                    while (ci < chunks.Length && chunks[ci].End <= absT) ci++;
                    if (ci < chunks.Length && absT >= chunks[ci].Start) gain = chunks[ci].Gain;
                }
                var eff = Math.Clamp(baseScale * gain, 0, 2.0);
                lastTopY = bottomY - peak * laneH * eff;
                g.LineTo(new Point(XOf(b0), lastTopY));   // vertex at the window's LEFT edge
            }
            // Carry the last window's height across its full width to the clip's right edge so the fill
            // reaches rect.Right (XOf(peaks.Length) == rect.Right) instead of stopping a window short.
            var rightX = XOf(Math.Min(peaks.Length, (kEnd + 1) * stride));
            g.LineTo(new Point(rightX, lastTopY));
            g.LineTo(new Point(rightX, bottomY));
            g.EndFigure(true);
        }
        // Antialiasing comes from DrawAudioLanes being called outside the Aliased scope in Render().
        using (ctx.PushClip(rect))
            ctx.DrawGeometry(_wave, null, geo);
    }

    private void DrawClipBoundaries(DrawingContext ctx, double h, double leftT, double rightT)
    {
        // a subtle full-height divider at each clip break
        for (var i = 1; i < _clips.Length; i++)
        {
            var b = _clips[i].Start;
            if (b < leftT || b > rightT) continue;
            var x = Math.Round(TimeToX(b)) + 0.5;
            ctx.DrawLine(_boundary, new Point(x, RulerHeight), new Point(x, h));
        }
    }

    // Translucent band + bright edges over the selected span, with its length shown above it.
    private void DrawSelection(DrawingContext ctx, double h)
    {
        if (!_selActive || _total <= 0) return;
        var x0 = TimeToX(_selStart);
        var x1 = TimeToX(_selEnd);
        if (x1 < 0 || x0 > Bounds.Width) return;
        var band = new Rect(Math.Max(-2, x0), RulerHeight, Math.Max(1, x1 - x0), Math.Max(0, h - RulerHeight));
        ctx.FillRectangle(_selFill, band);
        ctx.DrawLine(_selEdge, new Point(x0, RulerHeight), new Point(x0, h));
        ctx.DrawLine(_selEdge, new Point(x1, RulerHeight), new Point(x1, h));

        var secs = _selEnd - _selStart;
        var label = secs >= 60 ? $"◄ {(int)(secs / 60)}m {secs % 60:0}s ►" : $"◄ {secs:0.#}s ►";
        var ft = Ft(label, _hoverText);
        var lx = Math.Clamp((x0 + x1) / 2 - ft.Width / 2, 2, Math.Max(2, Bounds.Width - ft.Width - 2));
        var ly = RulerHeight + 2;
        ctx.FillRectangle(_hoverBg, new Rect(lx - 4, ly - 1, ft.Width + 8, ft.Height + 2));
        ctx.DrawText(ft, new Point(lx, ly));
    }

    private void DrawPlayhead(DrawingContext ctx, double h)
    {
        // Fractional x (no pixel-snapping) so the line moves sub-pixel smoothly with the interpolated
        // playhead; drawn antialiased (called outside the Aliased scope in Render).
        var x = TimeToX(_playhead);
        ctx.DrawLine(_play, new Point(x, 0), new Point(x, h));
        // a small handle at the top
        ctx.FillRectangle(_play.Brush!, new Rect(x - 4, 0, 8, 5));
    }

    private static Bitmap? NearestThumb(Bitmap[] bmps, double[] times, double t)
    {
        if (bmps.Length == 0) return null;
        // times is monotonically increasing → bisect for the closest entry by absolute distance.
        int lo = 0, hi = times.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (times[mid] < t) lo = mid + 1; else hi = mid;
        }
        var idx = lo;
        if (idx > 0 && Math.Abs(times[idx - 1] - t) < Math.Abs(times[idx] - t)) idx--;
        return bmps[idx];
    }

    private double ChooseTickStep()
    {
        var target = 90 / _pps; // aim ticks ~90px apart
        double[] steps =
        {
            0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800,
        };
        foreach (var s in steps) if (s >= target) return s;
        return steps[^1];
    }

    private FormattedText Ft(string text, IBrush brush)
    {
        var key = text + "" + (brush == _clipText ? "c" : "t");
        if (_ftCache.TryGetValue(key, out var ft)) return ft;
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _tf, 11, brush);
        if (_ftCache.Count > 512) _ftCache.Clear();
        _ftCache[key] = ft;
        return ft;
    }

    private static string FmtTick(double s)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, s));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}
