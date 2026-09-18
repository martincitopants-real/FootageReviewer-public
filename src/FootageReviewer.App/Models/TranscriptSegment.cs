using System.ComponentModel;
using Avalonia.Media;

namespace FootageReviewer.App.Models;

/// <summary>
/// One transcript line from the auto recognizer. <see cref="Start"/>/<see cref="End"/> are
/// timeline-absolute seconds (the per-source recognizer output is shifted by the clip's start).
/// <see cref="Speaker"/> is the audio-track label (e.g. "Mic", "Discord").
/// </summary>
public sealed class TranscriptSegment : INotifyPropertyChanged
{
    public double Start { get; init; }
    public double End { get; init; }
    public string Text { get; init; } = "";
    public string Speaker { get; init; } = "";

    public string Timecode => LogEntry.FormatTc(Start);

    private static readonly IBrush ActiveBg = new SolidColorBrush(Color.Parse("#27314A"));
    private static readonly IBrush InactiveBg = Brushes.Transparent;

    private bool _active;
    public bool IsActive
    {
        get => _active;
        set { if (_active != value) { _active = value; OnChanged(nameof(IsActive)); OnChanged(nameof(RowBackground)); } }
    }

    public IBrush RowBackground => _active ? ActiveBg : InactiveBg;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
