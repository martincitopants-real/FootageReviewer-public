using System;
using Avalonia.Controls;
using FootageReviewer.App.Models;

namespace FootageReviewer.App.Views;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; } = new();

    /// <summary>Fired live as the skip-distance value changes (for a preview before Save).</summary>
    public event Action<double>? SkipSecondsPreview;
    /// <summary>Fired live as the transcript font size changes (for a preview before Save).</summary>
    public event Action<double>? FontSizePreview;

    /// <summary>Set when the user clicked a "Regenerate …" button: "thumbs" or "waves" (else null). The owner
    /// runs the regeneration after the dialog closes (settings are still saved as if Save were clicked).</summary>
    public string? RequestedRegen { get; private set; }

    // The "Windows Default (…)" combo item; the bracketed name is the actual default capture device.
    private readonly string _micDefaultItem;

    public SettingsWindow() : this(new AppSettings(), null, null) { }

    public SettingsWindow(AppSettings current, System.Collections.Generic.IReadOnlyList<string>? microphones,
                          string? defaultMicName)
    {
        InitializeComponent();
        // Keyboard-shortcuts table — built from the single shared list so it always matches the
        // toolbar legend (no hand-maintained duplicate that can drift).
        ShortcutsList.ItemsSource = Util.KeyboardShortcuts.All;
        // Microphone list: "Windows Default (<real device>)" first, then the enumerated devices.
        _micDefaultItem = string.IsNullOrWhiteSpace(defaultMicName)
            ? "Windows Default"
            : $"Windows Default ({Util.WindowsAudio.ShortName(defaultMicName)})";
        MicCombo.Items.Add(_micDefaultItem);
        if (microphones != null) foreach (var m in microphones) MicCombo.Items.Add(m);
        var savedMic = current.DictationMic;
        if (!string.IsNullOrWhiteSpace(savedMic) && !MicCombo.Items.Contains(savedMic)) MicCombo.Items.Add(savedMic);
        MicCombo.SelectedItem = string.IsNullOrWhiteSpace(savedMic) ? _micDefaultItem : savedMic;
        RenderMediaLocations(current);
        AutoSaveCheck.IsChecked = current.AutoSaveEnabled;
        IntervalInput.Value = current.AutoSaveIntervalSeconds;
        ConfirmCloseCheck.IsChecked = current.ConfirmOnClose;
        TranscribeOnLoadCheck.IsChecked = current.TranscribeOnLoad;
        SkipSecondsInput.Value = (decimal)current.SkipSeconds;
        ScaleSkipCheck.IsChecked = current.ScaleSkipBySpeed;
        TranscriptFontInput.Value = (decimal)current.TranscriptFontSize;
        AutoLevelMaxInput.Value = (decimal)current.AutoLevelMax;
        CfgCopyOnlyNew.IsChecked = current.CopyOnlyUncopied;
        CfgCopyHeaders.IsChecked = current.CopyFolderHeaders;
        CfgCopyDayHeadings.IsChecked = current.CopyDayHeadings;
        CfgWarnOutOfOrder.IsChecked = current.WarnOnOutOfOrderEdit;
        CfgCopyTimestamps.IsChecked = current.CopyTimestamps;
        MergeWindowInput.Value = (decimal)current.LogMergeWindowSec;
        AutoCorrectCheck.IsChecked = current.AutoCorrectEnabled;
        AutoCapitalizeCheck.IsChecked = current.AutoCapitalizeEnabled;
        SpellCheckCheck.IsChecked = current.SpellCheckEnabled;
        Track2PrefixCheck.IsChecked = current.Track2PrefixEnabled;
        Track2PrefixInput.Text = current.Track2Prefix;
        Track3PrefixCheck.IsChecked = current.Track3PrefixEnabled;
        Track3PrefixInput.Text = current.Track3Prefix;
        OpenLastProjectCheck.IsChecked = current.OpenLastProjectOnLaunch;
        Result = current;

        // Live previews — fire as the user adjusts, so they see the effect before clicking Save.
        SkipSecondsInput.ValueChanged += (_, _) =>
        {
            if (SkipSecondsInput.Value is decimal v) SkipSecondsPreview?.Invoke((double)v);
        };
        TranscriptFontInput.ValueChanged += (_, _) =>
        {
            if (TranscriptFontInput.Value is decimal v) FontSizePreview?.Invoke((double)v);
        };

        // Mutate the existing settings object (so fields not exposed here are preserved), persist, and record
        // it as the result. Shared by Save and the Regenerate buttons.
        void Apply()
        {
            current.AutoSaveEnabled = AutoSaveCheck.IsChecked == true;
            current.AutoSaveIntervalSeconds = (int)(IntervalInput.Value ?? 60);
            current.ConfirmOnClose = ConfirmCloseCheck.IsChecked == true;
            current.TranscribeOnLoad = TranscribeOnLoadCheck.IsChecked == true;
            current.SkipSeconds = (double)(SkipSecondsInput.Value ?? 5m);
            current.ScaleSkipBySpeed = ScaleSkipCheck.IsChecked == true;
            current.TranscriptFontSize = (double)(TranscriptFontInput.Value ?? 11m);
            current.AutoLevelMax = (double)(AutoLevelMaxInput.Value ?? 0.9m);
            current.CopyOnlyUncopied = CfgCopyOnlyNew.IsChecked == true;
            current.CopyFolderHeaders = CfgCopyHeaders.IsChecked == true;
            current.CopyTimestamps = CfgCopyTimestamps.IsChecked == true;
            current.CopyDayHeadings = CfgCopyDayHeadings.IsChecked == true;
            current.WarnOnOutOfOrderEdit = CfgWarnOutOfOrder.IsChecked == true;
            current.LogMergeWindowSec = (double)(MergeWindowInput.Value ?? 5m);
            current.AutoCorrectEnabled = AutoCorrectCheck.IsChecked == true;
            current.AutoCapitalizeEnabled = AutoCapitalizeCheck.IsChecked == true;
            current.SpellCheckEnabled = SpellCheckCheck.IsChecked == true;
            current.DictationMic = (MicCombo.SelectedItem as string) is { } mic && mic != _micDefaultItem ? mic : null;
            current.Track2PrefixEnabled = Track2PrefixCheck.IsChecked == true;
            current.Track2Prefix = (Track2PrefixInput.Text ?? "").Trim();
            current.Track3PrefixEnabled = Track3PrefixCheck.IsChecked == true;
            current.Track3Prefix = (Track3PrefixInput.Text ?? "").Trim();
            current.OpenLastProjectOnLaunch = OpenLastProjectCheck.IsChecked == true;
            current.Save();
            Result = current;
        }

        SaveBtn.Click += (_, _) => { Apply(); Close(Result); };
        RegenThumbsBtn.Click += (_, _) => { Apply(); RequestedRegen = "thumbs"; Close(Result); };
        RegenWavesBtn.Click += (_, _) => { Apply(); RequestedRegen = "waves"; Close(Result); };
        CancelBtn.Click += (_, _) => Close(null);
    }

    /// <summary>List each per-machine mapping with a Remove button. Edits the live settings object,
    /// which Save persists along with everything else.</summary>
    private void RenderMediaLocations(AppSettings current)
    {
        MediaLocationsPanel.Children.Clear();
        var list = current.PathRemaps ??= new System.Collections.Generic.List<FootageReviewer.App.Util.PathRemap>();
        if (list.Count == 0)
        {
            MediaLocationsPanel.Children.Add(new TextBlock
            {
                Text = "None yet — open a project whose footage is elsewhere and choose “Locate” to add one.",
                Foreground = Avalonia.Media.Brush.Parse("#7E7E8C"), FontSize = 11,
            });
            return;
        }
        foreach (var r in list.ToArray())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var text = new TextBlock
            {
                Text = $"{r.From}  →  {r.To}", Foreground = Avalonia.Media.Brush.Parse("#E2E2EA"),
                FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var remove = new Button { Content = "Remove", FontSize = 11, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
            var captured = r;
            remove.Click += (_, _) => { list.Remove(captured); RenderMediaLocations(current); };
            Grid.SetColumn(remove, 1);
            row.Children.Add(text); row.Children.Add(remove);
            MediaLocationsPanel.Children.Add(row);
        }
    }
}
