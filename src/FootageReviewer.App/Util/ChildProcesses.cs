using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FootageReviewer.App.Util;

/// <summary>
/// All ffmpeg / ffprobe / python child processes start through here so we can both
///   (a) actively kill them when the user closes the app (KillAll), and
///   (b) let the OS kill them automatically if our process dies any other way
///       (Task Manager, hard crash) — see <see cref="EnsureJobObject"/>.
///
/// On Windows 8+ children inherit Job Object membership by default, so a JOB created here
/// "catches" every subprocess we spawn. Setting JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE means the
/// job (and every member process) dies with us.
/// </summary>
internal static class ChildProcesses
{
    private static readonly object _lock = new();
    private static readonly List<Process> _procs = new();
    private static IntPtr _job = IntPtr.Zero;

    public static Process Start(ProcessStartInfo psi)
    {
        var p = Process.Start(psi)!;
        try { p.EnableRaisingEvents = true; p.Exited += (_, _) => Untrack(p); } catch { }
        lock (_lock) _procs.Add(p);
        return p;
    }

    private static void Untrack(Process p)
    {
        lock (_lock) _procs.Remove(p);
    }

    public static void KillAll()
    {
        Process[] snap;
        lock (_lock) { snap = _procs.ToArray(); _procs.Clear(); }
        foreach (var p in snap)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
    }

    // Windows-only safety net: kill subprocesses if the parent dies for any reason.
    public static void EnsureJobObject()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (_job != IntPtr.Zero) return;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size)) return;
            if (!AssignProcessToJobObject(job, GetCurrentProcess())) return;
            _job = job; // intentionally leak the handle — closing it would kill us
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // ---- P/Invoke ---------------------------------------------------------

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }
}
