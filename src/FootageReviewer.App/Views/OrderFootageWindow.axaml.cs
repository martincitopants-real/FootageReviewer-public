using Avalonia.Controls;

namespace FootageReviewer.App.Views;

/// <summary>How "Order footage chronologically" decides each clip's position.</summary>
public enum ChronoBasis { Filename, Modified, Created }

public partial class OrderFootageWindow : Window
{
    public OrderFootageWindow()
    {
        InitializeComponent();
        CancelBtn.Click += (_, _) => Close(null);
        OrderBtn.Click += (_, _) => Close(
            ModifiedRadio.IsChecked == true ? ChronoBasis.Modified
            : CreatedRadio.IsChecked == true ? ChronoBasis.Created
            : ChronoBasis.Filename);
    }

    /// <summary>Optional one-line summary (clip count) shown above the options.</summary>
    public void SetSummary(string text) => SummaryText.Text = text;
}
