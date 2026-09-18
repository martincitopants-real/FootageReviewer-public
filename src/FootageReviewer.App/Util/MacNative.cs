using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FootageReviewer.App.Util;

/// <summary>
/// macOS shims for HanumanInstitute.LibMpv 0.9.1.
///
/// The package ships a MacFunctionResolver, but it inherits the Linux resolver's dlopen/dlsym/dlerror
/// P/Invokes, which are declared against "libdl.so.2" — a glibc library that does not exist on macOS
/// (the dl* family lives in libSystem). Loading libmpv therefore dies with DllNotFoundException before
/// it ever gets as far as looking for mpv itself.
///
/// We can't patch the package, but a DllImportResolver registered against *its* assembly lets us
/// redirect those libdl lookups to libSystem. We also pre-resolve libmpv to an absolute path, because
/// Homebrew installs into /opt/homebrew/lib, which is not on dyld's default search path.
/// </summary>
public static class MacNative
{
    private static bool _installed;

    /// <summary>Candidate absolute paths for Homebrew's libmpv, newest naming first.</summary>
    private static readonly string[] MpvCandidates =
    {
        "/opt/homebrew/lib/libmpv.2.dylib",
        "/opt/homebrew/lib/libmpv.dylib",
        "/usr/local/lib/libmpv.2.dylib",
        "/usr/local/lib/libmpv.dylib",
    };

    /// <summary>Absolute path to libmpv, or null if mpv isn't installed.</summary>
    public static string? MpvPath => Array.Find(MpvCandidates, File.Exists);

    /// <summary>
    /// Install the resolver. Must run before anything touches MpvContext, since a DllImportResolver
    /// can only be registered before the assembly's first P/Invoke (and only once).
    /// </summary>
    public static void Install()
    {
        if (_installed || !OperatingSystem.IsMacOS()) return;
        _installed = true;

        var libMpvAssembly = typeof(HanumanInstitute.LibMpv.MpvContext).Assembly;

        NativeLibrary.SetDllImportResolver(libMpvAssembly, (name, _, _) =>
        {
            // dlopen/dlsym/dlerror -> libSystem, which is always present and always loaded.
            if (name.StartsWith("libdl", StringComparison.OrdinalIgnoreCase))
                return NativeLibrary.Load("/usr/lib/libSystem.B.dylib");

            // Anything that looks like a request for mpv gets the real Homebrew dylib by absolute path.
            if (name.Contains("mpv", StringComparison.OrdinalIgnoreCase))
            {
                var path = MpvPath;
                if (path != null && NativeLibrary.TryLoad(path, out var handle)) return handle;
            }

            return IntPtr.Zero; // fall back to the default resolution rules
        });
    }

    // ---- Synchronous property reads ---------------------------------------
    //
    // LibMpv 0.9.1 reads properties by posting an async request and waiting for the reply on its event
    // loop. On macOS that reply never arrives, so the FIRST GetPropertyString blocks forever — which
    // silently kills duration, track-list/count (hence audio tracks, waveforms and the transcript) and
    // the playhead, while writes keep working because they are fire-and-forget.
    //
    // mpv_get_property_string is synchronous and thread-safe, so we call it straight through and skip
    // the event loop entirely.

    private delegate IntPtr GetPropertyStringFn(IntPtr ctx, IntPtr name);
    private delegate int MpvCommandFn(IntPtr ctx, IntPtr args);
    private delegate void MpvFreeFn(IntPtr data);

    private static GetPropertyStringFn? _getPropertyString;
    private static MpvFreeFn? _mpvFree;
    private static MpvCommandFn? _command;
    private static bool _fnsResolved;

    private static void EnsureFunctions()
    {
        if (_fnsResolved) return;
        _fnsResolved = true;
        try
        {
            // Prefer the copy shipped beside the binary (that is the one libmpv already loaded).
            var local = System.IO.Path.Combine(AppContext.BaseDirectory, "libmpv.so.2");
            var path = File.Exists(local) ? local : MpvPath;
            if (path == null || !NativeLibrary.TryLoad(path, out var lib)) return;

            if (NativeLibrary.TryGetExport(lib, "mpv_get_property_string", out var gp))
                _getPropertyString = Marshal.GetDelegateForFunctionPointer<GetPropertyStringFn>(gp);
            if (NativeLibrary.TryGetExport(lib, "mpv_free", out var fr))
                _mpvFree = Marshal.GetDelegateForFunctionPointer<MpvFreeFn>(fr);
            if (NativeLibrary.TryGetExport(lib, "mpv_command", out var cmd))
                _command = Marshal.GetDelegateForFunctionPointer<MpvCommandFn>(cmd);
        }
        catch { /* leave the delegates null; callers fall back to the binding */ }
    }

    /// <summary>True when the direct-read path is usable.</summary>
    public static bool CanReadProperties
    {
        get { EnsureFunctions(); return _getPropertyString != null; }
    }

    /// <summary>mpv_get_property_string on the raw handle. Null if unset or unavailable.</summary>
    public static string? GetPropertyString(IntPtr ctx, string name)
    {
        if (ctx == IntPtr.Zero) return null;
        EnsureFunctions();
        if (_getPropertyString == null) return null;

        var namePtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var result = _getPropertyString(ctx, namePtr);
            if (result == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(result); }
            finally { try { _mpvFree?.Invoke(result); } catch { /* leak one string rather than crash */ } }
        }
        catch { return null; }
        finally { Marshal.FreeCoTaskMem(namePtr); }
    }

    /// <summary>True when the direct-command path is usable.</summary>
    public static bool CanRunCommands
    {
        get { EnsureFunctions(); return _command != null; }
    }

    /// <summary>
    /// mpv_command with a NULL-terminated argv. Synchronous — unlike the binding's async command path,
    /// which waits on the event loop for a completion reply that never arrives on macOS, wedging the
    /// UI thread on the very first loadfile. Using the array form also sidesteps all command-string
    /// quoting issues with EDL URLs (they contain spaces, % and ;).
    /// </summary>
    public static bool Command(IntPtr ctx, params string[] args)
    {
        if (ctx == IntPtr.Zero) return false;
        EnsureFunctions();
        if (_command == null) return false;

        var ptrs = new IntPtr[args.Length + 1];
        var argv = IntPtr.Zero;
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            argv = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
            Marshal.Copy(ptrs, 0, argv, ptrs.Length);
            return _command(ctx, argv) >= 0; // mpv returns >=0 on success
        }
        catch { return false; }
        finally
        {
            if (argv != IntPtr.Zero) Marshal.FreeHGlobal(argv);
            foreach (var ptr in ptrs) if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }
}
