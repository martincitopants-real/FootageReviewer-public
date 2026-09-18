using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FootageReviewer.App.Util;

/// <summary>
/// Minimal Core Audio (MMDevice) interop to read the name of the Windows DEFAULT capture device, so the
/// dictation settings can show "Windows Default (HyperX…)" and recording can target the real default rather
/// than whatever ffmpeg happens to enumerate first. Everything is wrapped in try/catch → null on any failure,
/// so the app degrades gracefully (label without the brackets, recording falls back to the first device).
/// </summary>
public static class WindowsAudio
{
    /// <summary>Friendly name of the Windows default capture (microphone) endpoint, or null.</summary>
    [SupportedOSPlatformGuard("windows")]
    public static string? GetDefaultCaptureName()
    {
        if (!OperatingSystem.IsWindows()) return null;
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        IPropertyStore? store = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // eCapture = 1, eConsole = 0. Returns non-zero (E_NOTFOUND) when there's no default mic.
            if (en.GetDefaultAudioEndpoint(1, 0, out dev) != 0 || dev == null) return null;
            if (dev.OpenPropertyStore(0 /* STGM_READ */, out store) != 0 || store == null) return null;
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.pwszVal) : null; }
            finally { try { PropVariantClear(ref pv); } catch { /* ignore */ } }
        }
        catch { return null; }
        finally
        {
            if (store != null) Marshal.ReleaseComObject(store);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    /// <summary>The short label for a friendly name: the text inside the trailing "(…)" if present
    /// (e.g. "Microphone (HyperX QuadCast S)" → "HyperX QuadCast S"), else the whole name.</summary>
    public static string ShortName(string? friendly)
    {
        if (string.IsNullOrWhiteSpace(friendly)) return "";
        var open = friendly.LastIndexOf('(');
        var close = friendly.LastIndexOf(')');
        if (open >= 0 && close > open + 1) return friendly.Substring(open + 1, close - open - 1).Trim();
        return friendly.Trim();
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pvar);

    private static PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        // (remaining vtable methods unused — not declared since they're never called)
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        // (GetId / GetState unused)
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int cProps);
        int GetAt(int iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        // (SetValue / Commit unused)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public int pid; }

    // Only the fields we read. On x64 the union begins at offset 8 (vt + 6 reserved bytes).
    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }
}
