using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using FootageReviewer.App.Util;
using HanumanInstitute.LibMpv;

namespace FootageReviewer.App.Controls;

/// <summary>
/// Video surface for mpv, drawn through libmpv's render API into Avalonia's GL framebuffer.
///
/// Replaces HanumanInstitute.LibMpv.Avalonia's views. NativeView embeds mpv in a child native window
/// (Win32 only). OpenGlView never hands mpv a render context on macOS, so mpv falls back to creating
/// its OWN Cocoa window — and that window wants the main thread at the same moment our main thread is
/// blocked inside mpv, deadlocking the app on the first property read. Owning the render context here
/// keeps mpv drawing into this control on every platform.
/// </summary>
public class MpvVideoView : OpenGlControlBase
{
    public MpvContext? MpvContext { get; private set; }

    private IntPtr _renderCtx;
    private bool _renderFailed;

    // Kept alive for as long as mpv holds the pointers — a collected delegate is a hard crash.
    private MpvRender.GetProcAddressFn? _getProcAddress;
    private MpvRender.UpdateFn? _updateCallback;
    private GlInterface? _gl;

    public MpvVideoView()
    {
        try
        {
            MpvContext = new MpvContext();
            // The render API requires the libmpv video output; otherwise mpv builds its own window.
            MpvContext.SetPropertyString("vo", "libmpv");
        }
        catch (Exception ex) { DiagnosticsLogger.LogException("MpvVideoView: context init", ex); }
    }

    private IntPtr Handle()
    {
        if (MpvContext == null) return IntPtr.Zero;
        for (var t = MpvContext.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            var f = t.GetField("_ctx", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(MpvContext) is { } boxed)
                unsafe { return (IntPtr)Pointer.Unbox(boxed); }
        }
        return IntPtr.Zero;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = gl;
        if (_renderCtx != IntPtr.Zero || _renderFailed) return;

        var mpv = Handle();
        if (mpv == IntPtr.Zero || !MpvRender.Available)
        {
            _renderFailed = true;
            DiagnosticsLogger.Log($"MpvVideoView: render API unavailable (mpv={mpv != IntPtr.Zero}, api={MpvRender.Available})");
            return;
        }

        _getProcAddress = (_, name) =>
        {
            var n = Marshal.PtrToStringAnsi(name);
            if (string.IsNullOrEmpty(n) || _gl == null) return IntPtr.Zero;
            try { return _gl.GetProcAddress(n); } catch { return IntPtr.Zero; }
        };

        var init = new MpvRender.OpenGlInitParams
        {
            GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProcAddress),
            GetProcAddressCtx = IntPtr.Zero,
        };

        var apiType = Marshal.StringToCoTaskMemUTF8("opengl");
        var initPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvRender.OpenGlInitParams>());
        try
        {
            Marshal.StructureToPtr(init, initPtr, false);
            var parameters = new[]
            {
                new MpvRender.RenderParam { Type = MpvRender.ParamApiType, Data = apiType },
                new MpvRender.RenderParam { Type = MpvRender.ParamOpenGlInitParams, Data = initPtr },
                new MpvRender.RenderParam { Type = MpvRender.ParamInvalid, Data = IntPtr.Zero },
            };

            var rc = MpvRender.Create(out _renderCtx, mpv, parameters);
            if (rc < 0 || _renderCtx == IntPtr.Zero)
            {
                _renderFailed = true;
                DiagnosticsLogger.Log($"MpvVideoView: mpv_render_context_create failed rc={rc}");
                return;
            }

            // mpv signals from its own threads when a new frame (or a redraw) is ready; that callback is
            // the ONLY thing that drives painting. Re-requesting a frame at the end of every render
            // instead would spin the display link continuously and re-composite an unchanged 1440p
            // picture forever, burning ~70% CPU on an idle, paused player.
            _updateCallback = _ => Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
            MpvRender.SetUpdateCallback(_renderCtx, Marshal.GetFunctionPointerForDelegate(_updateCallback), IntPtr.Zero);

            DiagnosticsLogger.Log($"MpvVideoView: render context ready (GL {gl.Version}, {gl.Renderer})");
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiType);
            Marshal.FreeHGlobal(initPtr);
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        _gl = gl;
        if (_renderCtx == IntPtr.Zero) return;

        var scaling = (VisualRoot?.RenderScaling) ?? 1.0;
        var w = Math.Max(1, (int)(Bounds.Width * scaling));
        var h = Math.Max(1, (int)(Bounds.Height * scaling));

        var fbo = new MpvRender.OpenGlFbo { Fbo = fb, W = w, H = h, InternalFormat = 0 };
        var fboPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvRender.OpenGlFbo>());
        var flipPtr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.StructureToPtr(fbo, fboPtr, false);
            Marshal.WriteInt32(flipPtr, 1); // Avalonia's FBO is y-flipped relative to mpv
            var parameters = new[]
            {
                new MpvRender.RenderParam { Type = MpvRender.ParamOpenGlFbo, Data = fboPtr },
                new MpvRender.RenderParam { Type = MpvRender.ParamFlipY, Data = flipPtr },
                new MpvRender.RenderParam { Type = MpvRender.ParamInvalid, Data = IntPtr.Zero },
            };
            MpvRender.Render(_renderCtx, parameters);
        }
        finally
        {
            Marshal.FreeHGlobal(fboPtr);
            Marshal.FreeHGlobal(flipPtr);
        }

    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_renderCtx != IntPtr.Zero)
        {
            MpvRender.SetUpdateCallback(_renderCtx, IntPtr.Zero, IntPtr.Zero);
            MpvRender.Free(_renderCtx);
            _renderCtx = IntPtr.Zero;
        }
        _updateCallback = null;
        _getProcAddress = null;
        _gl = null;
    }
}
