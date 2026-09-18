namespace FootageReviewer.App.Models;

/// <summary>
/// A chapter divider on the timeline, one per source folder when footage is imported from a tree of
/// per-day folders. Distinct from a <see cref="LogEntry"/> marker: it is not a log line, it spans the
/// full height of the timeline and always shows its label.
///
/// Anchored to a clip rather than to a time. Clips get reordered and deleted, and a day heading that
/// stayed at a fixed timestamp would silently detach from the footage it names.
/// </summary>
public sealed class DayMarker
{
    /// <summary>Red — visually separate from the amber default of log markers.</summary>
    public const string DefaultColor = "#FF5A5A";

    /// <summary>Source path of the clip this day begins with.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Shown on the timeline; seeded from the folder name.</summary>
    public string Text { get; set; } = "";

    public string Color { get; set; } = DefaultColor;
}
