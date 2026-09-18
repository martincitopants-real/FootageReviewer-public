using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;

namespace FootageReviewer.App.Views;

/// <summary>Prompt for a new project base-name (no extension). Returns the chosen name, or null on cancel.</summary>
public partial class RenameProjectWindow : Window
{
    public RenameProjectWindow() : this("") { }

    public RenameProjectWindow(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        CancelBtn.Click += (_, _) => Close(null);
        RenameBtn.Click += (_, _) => Commit();
        NameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Commit()
    {
        var name = (NameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) { ShowError("Enter a name."); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { ShowError("That name has characters not allowed in a filename."); return; }
        Close(name);
    }

    private void ShowError(string msg) { ErrorText.Text = msg; ErrorText.IsVisible = true; }
}
