using System.Runtime.InteropServices;

namespace Sakin;

internal static class Native
{
    // ---------------- processes ----------------
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public const uint IDLE_PRIORITY_CLASS = 0x40;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    public const uint NORMAL_PRIORITY_CLASS = 0x20;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x8000;
    public const uint HIGH_PRIORITY_CLASS = 0x80;
    public const uint REALTIME_PRIORITY_CLASS = 0x100;

    public const int ProcessMemoryPriority = 0;
    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    public const uint MEMORY_PRIORITY_VERY_LOW = 1;
    public const uint MEMORY_PRIORITY_LOW = 2;
    public const uint MEMORY_PRIORITY_MEDIUM = 3;
    public const uint MEMORY_PRIORITY_BELOW_NORMAL = 4;
    public const uint MEMORY_PRIORITY_NORMAL = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetPriorityClass(IntPtr h, uint cls);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr h, int cls, ref MEMORY_PRIORITY_INFORMATION info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(IntPtr h, int cls, ref MEMORY_PRIORITY_INFORMATION info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessWorkingSetSizeEx(IntPtr h, IntPtr min, IntPtr max, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, char[] buffer, ref int size);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int length, out int returnLength);
    public const int SystemProcessInformation = 5;

    // ---------------- windows / foreground ----------------
    public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    public const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWA_TEXT_COLOR = 36;

    // ---------------- PDH (GPU engine counters) ----------------
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufferSize, out int itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    public static extern int PdhCloseQuery(IntPtr query);

    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    // ---------------- working-set pinning (Boost) ----------------
    public const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
    public const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2;
    public const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessWorkingSetSizeEx(IntPtr h, out IntPtr min, out IntPtr max, out uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    /// <summary>Enables a privilege the user already holds (e.g. SeIncreaseWorkingSetPrivilege, present but off by default).</summary>
    public static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 /*ADJUST_PRIVILEGES*/ | 0x0008 /*QUERY*/, out IntPtr tok)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 0x2 /*SE_PRIVILEGE_ENABLED*/ };
            return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(tok); }
    }

    // ---------------- global hotkey ----------------
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // ---------------- misc ----------------
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr h);
}
