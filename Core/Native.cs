using System.Runtime.InteropServices;

namespace Recorder;

public static class Native
{
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPOS { public IntPtr Hwnd, After; public int X, Y, Cx, Cy; public uint Flags; }

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
    public static readonly IntPtr Topmost = new(-1);
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct BASIC_LIMIT { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinWs, MaxWs; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct EXTENDED_LIMIT { public BASIC_LIMIT Basic; public IO_COUNTERS Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcess, PeakJob; }

    static readonly object JobGate = new();
    static IntPtr _job;

    public static void AttachChild(System.Diagnostics.Process p)
    {
        try
        {
            lock (JobGate)
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new EXTENDED_LIMIT();
                    info.Basic.LimitFlags = 0x2000;
                    int len = Marshal.SizeOf<EXTENDED_LIMIT>();
                    var ptr = Marshal.AllocHGlobal(len);
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(_job, 9, ptr, (uint)len);
                    Marshal.FreeHGlobal(ptr);
                }
                AssignProcessToJobObject(_job, p.Handle);
            }
        }
        catch (Exception e) { Log.Write("job: " + e.Message); }
    }
}
