using Avalonia.Controls;
using Avalonia.Input;

namespace FootageReviewer.App.Views;

/// <summary>
/// Generic single-line text prompt. Unlike RenameProjectWindow this imposes no filename rules — day
/// marker labels are free text, not paths. Returns the entered string, or null on cancel.
/// </summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow() : this("Name", "") { }

    public TextPromptWindow(string prompt, string current)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        ValueBox.Text = current;
        CancelBtn.Click += (_, _) => Close(null);
        OkBtn.Click += (_, _) => Commit();
        ValueBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
        };
        Opened += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    private void Commit()
    {
        var v = (ValueBox.Text ?? "").Trim();
        Close(string.IsNullOrWhiteSpace(v) ? null : v);
    }
}
