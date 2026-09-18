using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using FootageReviewer.App.ViewModels;
using FootageReviewer.App.Views;

namespace FootageReviewer.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Quit when the last window closes, not when one designated window does: the app hands off
            // between the launcher and the workspace, so either may legitimately be the only one open.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;

            var settings = Models.AppSettings.Load();
            if (settings.OpenLastProjectOnLaunch && !string.IsNullOrEmpty(settings.LastProjectPath))
            {
                // Opted out of the launcher: go straight to the workspace, which reopens the last project.
                desktop.MainWindow = new MainWindow(null, fromLauncher: false)
                {
                    DataContext = new MainWindowViewModel(),
                };
            }
            else
            {
                // Default: the launcher opens first. Crucially it does NOT build MainWindow, so startup
                // doesn't pay for mpv init or loading a multi-hour project off the NAS.
                desktop.MainWindow = new LauncherWindow(settings);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}