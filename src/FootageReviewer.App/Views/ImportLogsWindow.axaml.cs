using Avalonia.Controls;

namespace FootageReviewer.App.Views;

/// <summary>Result of the import dialog: the pasted block + how to read any 2-part timecodes.</summary>
public sealed class ImportLogsResult
{
    public string Text { get; init; } = "";
    public bool TwoPartIsHoursMinutes { get; init; }
}

public partial class ImportLogsWindow : Window
{
    public ImportLogsWindow()
    {
        InitializeComponent();
        PasteBox.TextChanged += (_, _) => UpdateAmbiguity();
        HmRadio.IsCheckedChanged += (_, _) => UpdateImportEnabled();
        MsRadio.IsCheckedChanged += (_, _) => UpdateImportEnabled();
        CancelBtn.Click += (_, _) => Close(null);
        ImportBtn.Click += (_, _) => Close(new ImportLogsResult
        {
            Text = PasteBox.Text ?? "",
            TwoPartIsHoursMinutes = HmRadio.IsChecked == true,
        });
        UpdateAmbiguity();
    }

    // Show the H:MM-vs-M:SS choice ONLY when the pasted block actually has ambiguous 2-part timecodes.
    private void UpdateAmbiguity()
    {
        AmbiguityPanel.IsVisible = Util.LogImport.HasTwoPartTimecodes(PasteBox.Text);
        UpdateImportEnabled();
    }

    private void UpdateImportEnabled()
    {
        var hasText = !string.IsNullOrWhiteSpace(PasteBox.Text);
        // When ambiguous, force an explicit pick before allowing the import.
        var choiceMade = !AmbiguityPanel.IsVisible || HmRadio.IsChecked == true || MsRadio.IsChecked == true;
        ImportBtn.IsEnabled = hasText && choiceMade;
    }
}
