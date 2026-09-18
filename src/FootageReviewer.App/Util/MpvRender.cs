using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FootageReviewer.App.Util;

/// <summary>
/// Direct bindings for libmpv's render API (mpv/render.h + render_gl.h).
///
/// HanumanInstitute.LibMpv.Avalonia doesn't wire this up on macOS, so mpv falls back to creating its
/// own window. Owning the render context lets mpv draw into whatever framebuffer Avalonia gives us.
/// </summary>
public static class MpvRender
{
    public const int ParamInvalid = 0;
    public const int ParamApiType = 1;
    public const int ParamOpenGlInitParams = 2;
    public const int ParamOpenGlFbo = 3;
    public const int ParamFlipY = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderParam
    {
        public int Type;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenGlInitParams
    {
        public IntPtr GetProcAddress;   // void *(*)(void *ctx, const char *name)
        public IntPtr GetProcAddressCtx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenGlFbo
    {
        public int Fbo;
        public int W;
        public int H;
        public int InternalFormat;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetProcAddressFn(IntPtr ctx, IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void UpdateFn(IntPtr ctx);

    private delegate int CreateFn(out IntPtr res, IntPtr mpv, [In] RenderParam[] parameters);
    private delegate int RenderFn(IntPtr ctx, [In] RenderParam[] parameters);
    private delegate void FreeFn(IntPtr ctx);
    private delegate void SetUpdateCallbackFn(IntPtr ctx, IntPtr callback, IntPtr callbackCtx);

    private static CreateFn? _create;
    private static RenderFn? _render;
    private static FreeFn? _free;
    private static SetUpdateCallbackFn? _setUpdate;
    private static bool _resolved;

    /// <summary>Platform filename for libmpv. macOS ships it under the Linux soname (see the csproj).</summary>
    private static string[] LibraryNames =>
        OperatingSystem.IsWindows() ? new[] { "libmpv-2.dll", "mpv-2.dll" }
        : new[] { "libmpv.so.2", "libmpv.2.dylib", "libmpv.dylib", "libmpv.so" };

    private static void Ensure()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            IntPtr lib = IntPtr.Zero;
            foreach (var name in LibraryNames)
            {
                var beside = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(beside) && NativeLibrary.TryLoad(beside, out lib)) break;
                if (NativeLibrary.TryLoad(name, out lib)) break;
                lib = IntPtr.Zero;
            }
            if (lib == IntPtr.Zero && MacNative.MpvPath is { } p) NativeLibrary.TryLoad(p, out lib);
            if (lib == IntPtr.Zero) return;

            if (NativeLibrary.TryGetExport(lib, "mpv_render_context_create", out var c))
                _create = Marshal.GetDelegateForFunctionPointer<CreateFn>(c);
            if (NativeLibrary.TryGetExport(lib, "mpv_render_context_render", out var r))
                _render = Marshal.GetDelegateForFunctionPointer<RenderFn>(r);
            if (NativeLibrary.TryGetExport(lib, "mpv_render_context_free", out var f))
                _free = Marshal.GetDelegateForFunctionPointer<FreeFn>(f);
            if (NativeLibrary.TryGetExport(lib, "mpv_render_context_set_update_callback", out var u))
                _setUpdate = Marshal.GetDelegateForFunctionPointer<SetUpdateCallbackFn>(u);
        }
        catch { /* leave null; the caller logs and degrades */ }
    }

    public static bool Available
    {
        get { Ensure(); return _create != null && _render != null; }
    }

    public static int Create(out IntPtr renderCtx, IntPtr mpvHandle, RenderParam[] parameters)
    {
        renderCtx = IntPtr.Zero;
        Ensure();
        return _create == null ? -1 : _create(out renderCtx, mpvHandle, parameters);
    }

    public static int Render(IntPtr renderCtx, RenderParam[] parameters)
    {
        Ensure();
        return _render == null ? -1 : _render(renderCtx, parameters);
    }

    public static void Free(IntPtr renderCtx)
    {
        Ensure();
        if (_free != null && renderCtx != IntPtr.Zero) _free(renderCtx);
    }

    public static void SetUpdateCallback(IntPtr renderCtx, IntPtr callback, IntPtr ctx)
    {
        Ensure();
        _setUpdate?.Invoke(renderCtx, callback, ctx);
    }
}
