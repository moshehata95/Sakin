using System.Runtime.InteropServices;

namespace Sakin;

/// <summary>One row of the system process table.</summary>
internal sealed class ProcRow
{
    public int Pid;
    public int ParentPid;
    public string Name = "";        // lower-case, no ".exe"
    public long CreateTime;
    public long CpuTime;            // user + kernel, 100ns units
    public long PrivateBytes;       // private working set (what Task Manager calls "Memory")
    public int SessionId;
}

/// <summary>
/// Reads the whole process table in one syscall (NtQuerySystemInformation).
/// Far cheaper than Process.GetProcesses(), and it needs no handle per process,
/// so it sees elevated processes too.
/// </summary>
internal static class ProcessTable
{
    static IntPtr _buf = IntPtr.Zero;
    static int _len = 0;

    public static List<ProcRow> Read()
    {
        if (_buf == IntPtr.Zero) { _len = 1 << 20; _buf = Marshal.AllocHGlobal(_len); }
        while (true)
        {
            int st = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buf, _len, out int need);
            if (st == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
            {
                Marshal.FreeHGlobal(_buf);
                _len = Math.Max(need + (64 << 10), _len * 2);
                _buf = Marshal.AllocHGlobal(_len);
                continue;
            }
            if (st != 0) return new List<ProcRow>();
            break;
        }

        var list = new List<ProcRow>(400);
        int off = 0;
        while (true)
        {
            IntPtr p = _buf + off;
            int next = Marshal.ReadInt32(p, 0);
            var r = new ProcRow
            {
                PrivateBytes = Marshal.ReadInt64(p, 8),
                CreateTime = Marshal.ReadInt64(p, 32),
                CpuTime = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48),
                Pid = (int)Marshal.ReadIntPtr(p, 80),
                ParentPid = (int)Marshal.ReadIntPtr(p, 88),
                SessionId = Marshal.ReadInt32(p, 100),
            };
            ushort nameLen = (ushort)Marshal.ReadInt16(p, 56);
            IntPtr nameBuf = Marshal.ReadIntPtr(p, 64);
            string name = nameBuf != IntPtr.Zero && nameLen > 0 ? Marshal.PtrToStringUni(nameBuf, nameLen / 2) : (r.Pid == 0 ? "idle" : "system");
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            r.Name = name.ToLowerInvariant();
            list.Add(r);
            if (next == 0) break;
            off += next;
        }
        return list;
    }
}

/// <summary>Per-process GPU utilisation from the "GPU Engine" performance counters (same source as Task Manager).</summary>
internal sealed class GpuProbe : IDisposable
{
    IntPtr _query, _counter;
    bool _ok;

    public GpuProbe()
    {
        try
        {
            _ok = Native.PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
               && Native.PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) == 0
               && Native.PdhCollectQueryData(_query) == 0;
        }
        catch { _ok = false; }
    }

    /// <summary>pid → busiest engine-type utilisation (0..100).</summary>
    public Dictionary<int, double> Sample()
    {
        var result = new Dictionary<int, double>();
        if (!_ok) return result;
        if (Native.PdhCollectQueryData(_query) != 0) return result;
        int size = 0;
        int st = Native.PdhGetFormattedCounterArray(_counter, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out int count, IntPtr.Zero);
        if (st != Native.PDH_MORE_DATA || size == 0) return result;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            st = Native.PdhGetFormattedCounterArray(_counter, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out count, buf);
            if (st != 0) return result;
            // pid → engtype → summed utilisation
            var perType = new Dictionary<(int, string), double>();
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buf + i * 24; // { LPWSTR name; { DWORD status; double value } }
                string? inst = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0));
                int cstatus = Marshal.ReadInt32(item, 8);
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                if (inst == null || cstatus != 0 || v <= 0) continue;
                // pid_1234_luid_0x00000000_0x0000C3A1_phys_0_eng_0_engtype_3D
                if (!inst.StartsWith("pid_")) continue;
                int us = inst.IndexOf('_', 4);
                if (us < 0 || !int.TryParse(inst.AsSpan(4, us - 4), out int pid)) continue;
                int et = inst.LastIndexOf("engtype_", StringComparison.Ordinal);
                string type = et >= 0 ? inst[(et + 8)..] : "";
                var k = (pid, type);
                perType[k] = perType.GetValueOrDefault(k) + v;
            }
            foreach (var kv in perType)
            {
                int pid = kv.Key.Item1;
                result[pid] = Math.Max(result.GetValueOrDefault(pid), Math.Min(100, kv.Value));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}

/// <summary>Which processes have an audio stream playing right now (calls, videos, lessons).</summary>
internal static class AudioProbe
{
    public static HashSet<int> ActivePids()
    {
        var set = new HashSet<int>();
        object? enumObj = null;
        try
        {
            enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!);
            var en = (IMMDeviceEnumerator)enumObj!;
            if (en.EnumAudioEndpoints(0 /*eRender*/, 1 /*DEVICE_STATE_ACTIVE*/, out var coll) != 0) return set;
            coll.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (coll.Item(i, out var dev) != 0) continue;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (dev.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out object mgrObj) != 0) { Marshal.ReleaseComObject(dev); continue; }
                var mgr = (IAudioSessionManager2)mgrObj;
                if (mgr.GetSessionEnumerator(out var sessions) == 0)
                {
                    sessions.GetCount(out int sc);
                    for (int s = 0; s < sc; s++)
                    {
                        if (sessions.GetSession(s, out var ctl) != 0) continue;
                        if (ctl is IAudioSessionControl2 c2 && c2.GetState(out int state) == 0 && state == 1 /*Active*/)
                        {
                            if (c2.IsSystemSoundsSession() != 0 && c2.GetProcessId(out uint pid) == 0 && pid != 0) set.Add((int)pid);
                        }
                        Marshal.ReleaseComObject(ctl);
                    }
                    Marshal.ReleaseComObject(sessions);
                }
                Marshal.ReleaseComObject(mgr);
                Marshal.ReleaseComObject(dev);
            }
            Marshal.ReleaseComObject(coll);
        }
        catch { /* no audio stack: treat as silent */ }
        finally { if (enumObj != null) Marshal.ReleaseComObject(enumObj); }
        return set;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr a, int b, out IntPtr c);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr a, int b, out IntPtr c);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(IntPtr g, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession(); // S_OK (0) means it IS the system-sounds session
    }
}
