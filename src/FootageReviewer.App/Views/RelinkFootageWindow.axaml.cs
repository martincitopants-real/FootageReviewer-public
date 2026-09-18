using System.Collections.Generic;
using System.IO;
using System.Linq;
using FootageReviewer.App.Util;
using Avalonia.Controls;

namespace FootageReviewer.App.Views;

/// <summary>What the user chose to do about footage the project can no longer find.</summary>
public enum RelinkChoice { OpenAnyway, RemoveMissing, Locate }

/// <summary>
/// Shown when a project's recordings aren't where it left them (the usual cause is the footage folder being
/// moved or renamed). Offers to point the project at the new folder, drop the dead clips, or carry on.
/// </summary>
public partial class RelinkFootageWindow : Window
{
    public RelinkFootageWindow() : this(new List<string>(), 0, 0) { }

    /// <param name="missing">Absolute paths that no longer resolve.</param>
    /// <param name="total">How many clips the project has in total.</param>
    /// <param name="duplicates">How many of the missing ones are already in the project under a working path.</param>
    public RelinkFootageWindow(IReadOnlyList<string> missing, int total, int duplicates)
    {
        InitializeComponent();

        HeadlineText.Text = missing.Count == total
            ? "None of this project's footage can be found"
            : $"{missing.Count} of {total} clips can't be found";

        SummaryText.Text = duplicates > 0
            ? $"{duplicates} of them are already in this project under a path that still works, so they look like " +
              "leftovers from a move — removing those loses nothing."
            : "This usually means the footage folder was moved or renamed. Point the project at the new folder " +
              "and the clips will be matched up by filename.";

        // Show the folders first (that's the actionable part), then the filenames.
        var folders = missing.Select(p => PathText.DirectoryName(p)) // either OS's separators
                             .Distinct()
                             .OrderBy(x => x)
                             .ToList();
        var lines = new List<string>();
        foreach (var f in folders.Take(4)) lines.Add(f + (f.Contains('\\') ? '\\' : '/'));
        if (folders.Count > 4) lines.Add($"…and {folders.Count - 4} more folders");
        lines.Add("");
        foreach (var p in missing.Take(12)) lines.Add("  " + PathText.FileName(p));
        if (missing.Count > 12) lines.Add($"  …and {missing.Count - 12} more files");
        MissingList.Text = string.Join("\n", lines);

        HintText.Text = "Locate folder… searches the folder you pick (and its subfolders) for these filenames. " +
                        "Your logs, markers and sync points are keyed to time, so they're unaffected either way.";

        // When every missing clip is a duplicate of one that still works, relinking would add each file to
        // the project twice — so make removing them the obvious (highlighted) action instead.
        if (duplicates > 0 && duplicates == missing.Count)
        {
            RemoveBtn.Background = Avalonia.Media.Brush.Parse("#274050");
            RemoveBtn.Foreground = Avalonia.Media.Brushes.White;
            RemoveBtn.Content = $"Remove {missing.Count} leftover clip(s)";
            LocateBtn.Background = null;
            LocateBtn.Foreground = Avalonia.Media.Brush.Parse("#9A9AA8");
            HintText.Text = "These files are already in the project under a working path, so removing the " +
                            "leftovers is all that's needed. Your logs, markers and sync points are keyed to " +
                            "time and aren't affected.";
        }

        OpenAnywayBtn.Click += (_, _) => Close(RelinkChoice.OpenAnyway);
        RemoveBtn.Click += (_, _) => Close(RelinkChoice.RemoveMissing);
        LocateBtn.Click += (_, _) => Close(RelinkChoice.Locate);
    }
}
