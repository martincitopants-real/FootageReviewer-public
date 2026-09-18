using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FootageReviewer.App.Models;
using FootageReviewer.App.Util;

namespace FootageReviewer.App.Views;

/// <summary>
/// The window the app opens with: a list of every project you've worked on, with a poster frame and its
/// stats. Deliberately does NOT construct the workspace (MainWindow) — that's the slow part (mpv init plus
/// loading a multi-hour project off the NAS), and keeping it out of startup is the whole point of this screen.
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<ProjectCard> _cards = new();
    private List<ProjectCard> _all = new();
    private CancellationTokenSource? _indexCts;
    private bool _launching;

    public LauncherWindow() : this(AppSettings.Load()) { }

    public LauncherWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        ProjectList.ItemsSource = _cards;
        AutoOpenChk.IsChecked = _settings.OpenLastProjectOnLaunch;
        AutoOpenChk.IsCheckedChanged += (_, _) =>
        {
            _settings.OpenLastProjectOnLaunch = AutoOpenChk.IsChecked == true;
            _settings.Save();
        };

        NewProjectBtn.Click += (_, _) => Launch(null);            // null = start an empty project
        OpenProjectBtn.Click += async (_, _) => await BrowseAsync();
        FilterBox.TextChanged += (_, _) => ApplyFilter();

        Opened += (_, _) => LoadProjects();
        Closed += (_, _) => _indexCts?.Cancel();
    }

    // ---- list ---------------------------------------------------------------------------------------

    private void LoadProjects()
    {
        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;

        // Paint instantly from the recent list; enrich in the background.
        var paths = (_settings.RecentProjects ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _all = paths.Select(ProjectIndex.Stub).ToList();
        ApplyFilter();
        EmptyHint.IsVisible = _all.Count == 0;
        SubHeader.Text = _all.Count == 0
            ? "No projects yet"
            : $"{_all.Count} project{(_all.Count == 1 ? "" : "s")}";

        _ = Task.Run(() => IndexAllAsync(ct), ct);
    }

    /// <summary>
    /// Fill in clip/log counts, duration and the poster frame. Newest first, so the project you're most
    /// likely to click resolves first. Cached projects are skipped, so this only costs anything once.
    /// </summary>
    private async Task IndexAllAsync(CancellationToken ct)
    {
        // Snapshot: the UI thread swaps indexed cards into _all (and LoadProjects can replace it outright),
        // and enumerating the live list while that happens throws "Collection was modified".
        var snapshot = _all.ToList();
        var stale = snapshot.Where(c => !ProjectIndex.IsCacheFresh(c.Path)).ToList();
        var done = 0;
        foreach (var card in snapshot)
        {
            if (ct.IsCancellationRequested) return;
            if (ProjectIndex.IsCacheFresh(card.Path)) continue;

            var fresh = ProjectIndex.Index(card.Path);
            done++;
            var progress = $"reading project {done} of {stale.Count}…";
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                // Swap the stub for the indexed card (ObservableCollection repaints just that row).
                var i = _all.FindIndex(x => string.Equals(x.Path, fresh.Path, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) _all[i] = fresh;
                var j = _cards.ToList().FindIndex(x => string.Equals(x.Path, fresh.Path, StringComparison.OrdinalIgnoreCase));
                if (j >= 0) _cards[j] = fresh;
                StatusText.Text = done < stale.Count ? progress : "";
            });
        }
        await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = "");
    }

    private void ApplyFilter()
    {
        var q = (FilterBox.Text ?? "").Trim();
        _cards.Clear();
        foreach (var c in _all)
        {
            if (q.Length > 0 &&
                c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                c.Folder.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _cards.Add(c);
        }
    }

    // ---- actions ------------------------------------------------------------------------------------

    private void OnProjectClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string path }) return;
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed) { ShowCardMenu(sender as Control, path); e.Handled = true; return; }
        if (!pt.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        Launch(path);
    }

    private void ShowCardMenu(Control? anchor, string path)
    {
        if (anchor == null) return;
        var menu = new ContextMenu();

        var reveal = new MenuItem { Header = "Reveal in Explorer" };
        reveal.Click += (_, _) =>
        {
            try
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (Directory.Exists(Path.GetDirectoryName(path)!))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
                else StatusText.Text = "that folder isn't reachable";
            }
            catch (Exception ex) { DiagnosticsLogger.LogException("reveal in explorer", ex); }
        };

        var remove = new MenuItem { Header = "Remove from list" };
        remove.Click += (_, _) =>
        {
            _settings.RecentProjects?.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_settings.LastProjectPath, path, StringComparison.OrdinalIgnoreCase))
                _settings.LastProjectPath = null;
            _settings.Save();
            ProjectIndex.Forget(path);
            LoadProjects();
        };
        // Deliberately no "delete from disk" — that belongs in Explorer, not one misclick away from a
        // project full of logs.

        menu.Items.Add(reveal);
        menu.Items.Add(remove);
        menu.Open(anchor);
    }

    private async Task BrowseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Footage Reviewer project") { Patterns = new[] { "*.frproj" } },
                FilePickerFileTypes.All,
            },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path)) Launch(path);
    }

    /// <summary>Hand over to the workspace: build MainWindow, show it, and close this window.</summary>
    private void Launch(string? projectPath)
    {
        if (_launching) return;         // double-click shouldn't build two workspaces
        _launching = true;
        _indexCts?.Cancel();
        try
        {
            var main = new MainWindow(projectPath);
            main.Show();
            Close();
        }
        catch (Exception ex)
        {
            _launching = false;
            DiagnosticsLogger.LogException("launch workspace", ex);
            StatusText.Text = "couldn't open that project (logged to diagnostics.log)";
        }
    }
}
