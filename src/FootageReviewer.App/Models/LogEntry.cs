using System;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace FootageReviewer.App.Models;

/// <summary>
/// One manual (later: transcript-derived) log entry. <see cref="T"/> is the CANONICAL video time in
/// seconds and is never mutated by the timer offset — the offset is applied only at display/export so
/// changing it later can't corrupt the entries. <see cref="DisplayTimecode"/> is T + offset.
/// </summary>
public sealed class LogEntry : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    private double _t;
    public double T
    {
        get => _t;
        set { if (Math.Abs(_t - value) > 1e-9) { _t = value; OnChanged(nameof(T)); OnChanged(nameof(DisplayTimecode)); OnChanged(nameof(DisplayEditable)); } }
    }

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public string Source { get; init; } = "manual"; // "manual" | "transcript" (future)

    // Timeline marker colour (hex). Right-click a marker to change it.
    public const string DefaultColor = "#FFCB5C";
    private string _color = DefaultColor;
    public string Color
    {
        get => _color;
        set { if (_color != value) { _color = value; OnChanged(nameof(Color)); OnChanged(nameof(RowBackground)); } }
    }

    // "Copied" flag: set after Copy all so the next Copy all can skip it. Renders struck-through + dim.
    private bool _copied;
    public bool Copied
    {
        get => _copied;
        set
        {
            if (_copied == value) return;
            _copied = value;
            OnChanged(nameof(Copied));
            OnChanged(nameof(Strike));
            OnChanged(nameof(RowOpacity));
            OnChanged(nameof(CopyToggleGlyph));
        }
    }
    public TextDecorationCollection? Strike => _copied ? TextDecorations.Strikethrough : null;
    public double RowOpacity => _copied ? 0.45 : 1.0;
    public string CopyToggleGlyph => _copied ? "☑" : "☐";

    // Manual-log search highlight.
    private static readonly IBrush DefaultRowBrush = new ImmutableSolidColorBrush(Avalonia.Media.Color.Parse("#13131A"));
    private static readonly IBrush MatchRowBrush = new ImmutableSolidColorBrush(Avalonia.Media.Color.Parse("#3A3320"));
    // "The playhead is inside this log's window" — a violet used nowhere else in the UI (the palette is
    // otherwise blues/teals for clips, amber for markers/search, red for the playhead, green for copy targets).
    private static readonly IBrush NearRowBrush = new ImmutableSolidColorBrush(Avalonia.Media.Color.Parse("#3B2A5C"));
    private bool _searchMatch;
    public bool SearchMatch
    {
        get => _searchMatch;
        set { if (_searchMatch != value) { _searchMatch = value; OnChanged(nameof(RowBackground)); } }
    }

    private bool _nearPlayhead;
    /// <summary>True while the playhead is within a few seconds of this entry (set by the playback tick).</summary>
    public bool NearPlayhead
    {
        get => _nearPlayhead;
        set { if (_nearPlayhead != value) { _nearPlayhead = value; OnChanged(nameof(RowBackground)); } }
    }

    // Row background: search match wins, then "near the playhead"; else a subtle tint of the (non-default)
    // marker colour; else default.
    public IBrush RowBackground
    {
        get
        {
            if (_searchMatch) return MatchRowBrush;
            if (_nearPlayhead) return NearRowBrush;
            if (!string.IsNullOrEmpty(_color) && !string.Equals(_color, DefaultColor, StringComparison.OrdinalIgnoreCase))
            {
                try { var c = Avalonia.Media.Color.Parse(_color); return new ImmutableSolidColorBrush(new Avalonia.Media.Color(0x3A, c.R, c.G, c.B)); }
                catch { /* fall through */ }
            }
            return DefaultRowBrush;
        }
    }

    private string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnChanged(nameof(Text)); } }
    }

    private double _offset;
    public double OffsetSeconds
    {
        get => _offset;
        set { if (Math.Abs(_offset - value) > 1e-9) { _offset = value; OnChanged(nameof(DisplayTimecode)); OnChanged(nameof(DisplayEditable)); } }
    }

    public string DisplayTimecode => FormatTc(T + _offset);

    /// <summary>Editable display value (text-box backing). Setter parses and writes back to T.</summary>
    public string DisplayEditable
    {
        get => FormatTc(T + _offset);
        set { /* re-parse path lives in the view code so it can validate via the offset model */ }
    }

    // When ≥2 entries are merged (auto-merge within the window, or manual merge up/down) this holds the
    // original (time, text) of each part so the merge can be split back apart. Null = not a merged entry.
    private System.Collections.Generic.List<MergePart>? _mergedParts;
    public System.Collections.Generic.List<MergePart>? MergedParts
    {
        get => _mergedParts;
        set { _mergedParts = value; OnChanged(nameof(MergedParts)); OnChanged(nameof(IsMerged)); }
    }
    public bool IsMerged => _mergedParts is { Count: > 1 };

    private bool _editing;
    public bool IsEditing
    {
        get => _editing;
        set { if (_editing != value) { _editing = value; OnChanged(nameof(IsEditing)); OnChanged(nameof(IsNotEditing)); } }
    }
    public bool IsNotEditing => !_editing;

    public static string FormatTc(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var ts = TimeSpan.FromSeconds(s);
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>One original component of a merged log entry (its canonical time + text).</summary>
public sealed class MergePart
{
    public double T { get; set; }
    public string Text { get; set; } = "";
}
