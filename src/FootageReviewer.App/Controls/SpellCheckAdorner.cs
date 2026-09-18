using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace FootageReviewer.App.Controls;

/// <summary>
/// A transparent overlay that draws red squiggle underlines under misspelled words of a sibling TextBox.
/// It reads the TextBox's inner TextPresenter.TextLayout to map character ranges to on-screen rectangles
/// (so wrapping + scrolling are handled by the framework). It never modifies text and is not hit-testable;
/// every access to the (template-internal) presenter is guarded so a framework change degrades to "no
/// squiggles" rather than breaking the log input.
/// </summary>
public sealed class SpellCheckAdorner : Control
{
    private TextPresenter? _presenter;
    private ScrollViewer? _scroll;
    private (int start, int len)[] _spans = Array.Empty<(int, int)>();
    private static readonly ImmutablePen SquigglePen = new(new ImmutableSolidColorBrush(Color.Parse("#E0405A")), 1.3);

    public SpellCheckAdorner()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Wire the overlay to a TextBox: grab its presenter/scroll-viewer once the template applies.</summary>
    public void Attach(TextBox box)
    {
        box.TemplateApplied += (_, e) =>
        {
            try
            {
                _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
                var sv = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
                if (sv != null && !ReferenceEquals(sv, _scroll))
                {
                    _scroll = sv;
                    sv.ScrollChanged += (_, _) => InvalidateVisual();
                }
            }
            catch { _presenter = null; }
            InvalidateVisual();
        };
        // Text edits invalidate the (debounced) spans immediately: clear them so we never paint a stale
        // squiggle under the wrong characters between a keystroke and the next recompute. The owner pushes
        // fresh spans via SetSpans once its debounce fires.
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                _spans = Array.Empty<(int, int)>();
                InvalidateVisual();
            }
        };
    }

    public void SetSpans((int start, int len)[] spans)
    {
        _spans = spans ?? Array.Empty<(int, int)>();
        InvalidateVisual();
    }

    /// <summary>Character index under a point (in this overlay's coordinates), or null.</summary>
    public int? CharIndexAt(Point pInAdorner)
    {
        var pres = _presenter;
        if (pres?.TextLayout == null) return null;
        if (this.TranslatePoint(pInAdorner, pres) is not { } pp) return null;
        try
        {
            var hit = pres.TextLayout.HitTestPoint(pp);
            return hit.TextPosition;
        }
        catch { return null; }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var pres = _presenter;
        if (pres?.TextLayout is not { } layout || _spans.Length == 0) return;
        int textLen = pres.Text?.Length ?? 0;
        if (textLen == 0) return;

        foreach (var (start, len) in _spans)
        {
            if (start < 0 || len <= 0 || start + len > textLen) continue;
            IEnumerable<Rect> rects;
            try { rects = layout.HitTestTextRange(start, len); }
            catch { continue; }
            foreach (var r in rects)
            {
                var a = pres.TranslatePoint(new Point(r.X, r.Bottom), this);
                var b = pres.TranslatePoint(new Point(r.Right, r.Bottom), this);
                if (a is { } p0 && b is { } p1 && p1.X > p0.X)
                    DrawSquiggle(ctx, p0, p1);
            }
        }
    }

    private static void DrawSquiggle(DrawingContext ctx, Point a, Point b)
    {
        const double amp = 1.2, halfWave = 2.0;
        double y = a.Y - 1.0;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(a.X, y), false);
            bool up = true;
            for (double x = a.X + halfWave; x <= b.X; x += halfWave)
            {
                g.LineTo(new Point(Math.Min(x, b.X), y + (up ? -amp : amp)));
                up = !up;
            }
        }
        ctx.DrawGeometry(null, SquigglePen, geo);
    }
}
