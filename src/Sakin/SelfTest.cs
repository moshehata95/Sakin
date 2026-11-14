using System.Diagnostics;
using System.Text;

namespace Sakin;

/// <summary>
/// `Sakin.exe --selftest out.txt` checks every probe against Windows' own APIs and
/// round-trips a calm/restore on a throwaway process. Used for verification only.
/// </summary>
internal static class SelfTest
{
    public static int Run(string outPath)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);
        int fails = 0;
        void Check(bool ok, string what) { W((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

        // 1. process table vs Process.GetProcesses
        var rows = ProcessTable.Read();
        var me = rows.FirstOrDefault(r => r.Pid == Environment.ProcessId);
        Check(rows.Count > 50, $"process table read {rows.Count} rows");
        Check(me != null && me.Name == "sakin", $"found self by pid, name='{me?.Name}'");
        using (var cur = Process.GetCurrentProcess())
        {
            Check(me != null && me.SessionId == cur.SessionId, $"session id {me?.SessionId} == {cur.SessionId}");
            Check(me != null && me.CreateTime == cur.StartTime.ToFileTimeUtc(), $"create time matches StartTime");
            Check(me != null && Math.Abs(me.CpuTime - cur.TotalProcessorTime.Ticks) < 5_000_000, $"cpu time {me?.CpuTime} ≈ {cur.TotalProcessorTime.Ticks}");
            Check(me != null && me.ParentPid > 0 && rows.Any(r => r.Pid == me.ParentPid), $"parent pid {me?.ParentPid} exists");
        }
        int matched = 0, total = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var r = rows.FirstOrDefault(x => x.Pid == p.Id);
                if (r == null) continue;
                total++;
                if (string.Equals(r.Name, p.ProcessName, StringComparison.OrdinalIgnoreCase)) matched++;
            }
            catch { }
            finally { p.Dispose(); }
        }
        Check(total > 0 && matched >= total * 0.97, $"names match Process.GetProcesses for {matched}/{total}");

        // private working set sanity: sum must be below physical RAM
        var m = new Native.MEMORYSTATUSEX { dwLength = 64 };
        Native.GlobalMemoryStatusEx(ref m);
        double sumPriv = rows.Sum(r => r.PrivateBytes) / 1048576.0;
        double totalMB = m.ullTotalPhys / 1048576.0;
        Check(sumPriv > 500 && sumPriv < totalMB, $"sum private WS {sumPriv:0} MB < RAM {totalMB:0} MB, avail {m.ullAvailPhys / 1048576.0:0} MB");
        foreach (var r in rows.OrderByDescending(r => r.PrivateBytes).Take(8)) W($"     {r.Name,-28} {r.PrivateBytes / 1048576.0,8:0} MB  pid {r.Pid}");

        // 2. GPU probe
        using (var gpu = new GpuProbe())
        {
            Thread.Sleep(1100);
            var g = gpu.Sample();
            W($"INFO gpu probe: {g.Count} processes with GPU activity; top: " +
              string.Join(", ", g.OrderByDescending(k => k.Value).Take(4).Select(k => $"{rows.FirstOrDefault(r => r.Pid == k.Key)?.Name ?? k.Key.ToString()}={k.Value:0.0}%")));
        }

        // 3. audio probe
        try
        {
            var a = AudioProbe.ActivePids();
            W($"INFO audio probe: {a.Count} active sessions: " + string.Join(", ", a.Select(pid => rows.FirstOrDefault(r => r.Pid == pid)?.Name ?? pid.ToString())));
            Check(true, "audio probe ran without throwing");
        }
        catch (Exception ex) { Check(false, "audio probe: " + ex.Message); }

        // 4. calm + restore round trip on a throwaway process
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false };
        using (var victim = Process.Start(psi)!)
        {
            Thread.Sleep(300);
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, victim.Id);
            Check(h != IntPtr.Zero, "open throwaway process");
            uint prio0 = Native.GetPriorityClass(h);
            var mp0 = new Native.MEMORY_PRIORITY_INFORMATION();
            bool gm = Native.GetProcessInformation(h, Native.ProcessMemoryPriority, ref mp0, 4);
            var pw0 = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            bool gp = Native.GetProcessInformation(h, Native.ProcessPowerThrottling, ref pw0, 12);
            W($"INFO original: prio=0x{prio0:X} mem={mp0.MemoryPriority} (read={gm}) power ctl={pw0.ControlMask} st={pw0.StateMask} (read={gp})");

            Check(Native.SetPriorityClass(h, Native.IDLE_PRIORITY_CLASS) && Native.GetPriorityClass(h) == Native.IDLE_PRIORITY_CLASS, "set idle priority");
            var mp = new Native.MEMORY_PRIORITY_INFORMATION { MemoryPriority = Native.MEMORY_PRIORITY_VERY_LOW };
            Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref mp, 4);
            var mpr = new Native.MEMORY_PRIORITY_INFORMATION();
            Native.GetProcessInformation(h, Native.ProcessMemoryPriority, ref mpr, 4);
            Check(mpr.MemoryPriority == Native.MEMORY_PRIORITY_VERY_LOW, $"memory priority very low (read back {mpr.MemoryPriority})");
            var eco = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 1 };
            Check(Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref eco, 12), "efficiency mode (EcoQoS) on");
            var ecor = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            Native.GetProcessInformation(h, Native.ProcessPowerThrottling, ref ecor, 12);
            Check(ecor.ControlMask == 1 && ecor.StateMask == 1, $"efficiency mode read back ctl={ecor.ControlMask} st={ecor.StateMask}");
            Check(Native.SetProcessWorkingSetSizeEx(h, (IntPtr)(-1), (IntPtr)(-1), 0), "trim working set");

            // restore
            Native.SetPriorityClass(h, prio0);
            var mpb = new Native.MEMORY_PRIORITY_INFORMATION { MemoryPriority = gm ? mp0.MemoryPriority : 5 };
            Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref mpb, 4);
            var pwb = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = pw0.ControlMask, StateMask = pw0.StateMask };
            Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref pwb, 12);
            var mpc = new Native.MEMORY_PRIORITY_INFORMATION();
            Native.GetProcessInformation(h, Native.ProcessMemoryPriority, ref mpc, 4);
            var pwc = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            Native.GetProcessInformation(h, Native.ProcessPowerThrottling, ref pwc, 12);
            Check(Native.GetPriorityClass(h) == prio0 && mpc.MemoryPriority == mpb.MemoryPriority && pwc.ControlMask == pw0.ControlMask,
                  $"restored exactly: prio=0x{Native.GetPriorityClass(h):X} mem={mpc.MemoryPriority} ctl={pwc.ControlMask}");
            Native.CloseHandle(h);
            try { victim.Kill(true); } catch { }
        }

        W(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        File.WriteAllText(outPath, sb.ToString());
        return fails;
    }
}
