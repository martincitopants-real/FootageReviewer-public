using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Native;
using Avalonia.Win32;
using FootageReviewer.App.Util;

namespace FootageReviewer.App;

sealed class Program
{
    private static Mutex? _instanceMutex;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Crash diagnostics first, so anything that goes wrong below is captured. UI-thread crashes bubble up
        // to AppDomain.UnhandledException, so this catches the "moving clips crashed" class of bug.
        DiagnosticsLogger.InstallGlobalHandlers();
        DiagnosticsLogger.Log("=== app opened ===");

        // Single instance — avoids duplicate windows fighting over the same files.
        _instanceMutex = new Mutex(true, "FootageReviewer.SingleInstance", out var isNew);
        if (!isNew) return;

        // Put us in a Job Object so EVERY ffmpeg/ffprobe/python we spawn dies if we die,
        // even on hard kill / crash / Task Manager. The active KillAll() in MainWindow is the
        // belt; this is the suspenders.
        ChildProcesses.EnsureJobObject();

        // macOS: redirect LibMpv's Linux-only libdl P/Invokes to libSystem and point it at Homebrew's
        // libmpv. Must happen before the first MpvContext is constructed (MainWindow's XAML does that).
        MacNative.Install();

        // Last-ditch sweep for any subprocess that somehow escaped the job (or was started by
        // a library we didn't go through Process.Start for).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ChildProcesses.KillAll();

        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { DiagnosticsLogger.LogException("StartWithClassicDesktopLifetime", ex); throw; }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

        // The libmpv OpenGL renderer composites through ANGLE (GLES -> D3D11), so Avalonia
        // must use the AngleEgl backend on Windows for the in-tree GL surface to work.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
                // NOT WinUIComposition. Avalonia's WinUiCompositorConnection runs its own COM/DirectComposition
                // message loop on a separate thread, and 11.3.17 died there (0xC0000005 in combase.dll) after
                // five days of use while the timeline was being scrubbed hard - a known-fragile path with a
                // documented workaround of not using WinUI composition. The DXGI swap chain mode has no
                // compositor thread at all, still works with the ANGLE GL surface mpv renders into, and has
                // lower latency; RedirectionSurface is the fallback if a machine can't create the swap chain.
                CompositionMode = new[] { Win32CompositionMode.LowLatencyDxgiSwapChain, Win32CompositionMode.RedirectionSurface },
            });
        }

        // macOS: Avalonia's native backend defaults to Metal, so OpenGlControlBase never gets a GL
        // context — LibMpv's OpenGlView therefore never hands mpv a render context, mpv falls back to
        // making its OWN Cocoa window (vo=gpu-next), and that window wants the main thread at the same
        // moment our main thread is inside mpv_get_property_string holding/awaiting the core lock.
        // The result is a hard deadlock on the first property read. Forcing the GL path keeps mpv
        // rendering into our surface instead, exactly like AngleEgl does on Windows.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }
            });
        }

        return builder.LogToTrace();
    }
}
