using System.Collections.ObjectModel;
using System.ComponentModel;

namespace FootageReviewer.App.Models;

/// <summary>Manual-log "folder" — one per clip on the timeline. Entries fall under the clip whose
/// time range contains their <see cref="LogEntry.T"/>. Used for both display and copy-all output.</summary>
public sealed class ClipGroup : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public double Start { get; init; }
    public ObservableCollection<LogEntry> Entries { get; } = new();

    private bool _expanded = true;
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            OnChanged(nameof(IsExpanded));
            OnChanged(nameof(ToggleGlyph));
        }
    }

    public string ToggleGlyph => _expanded ? "▼" : "▶";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
