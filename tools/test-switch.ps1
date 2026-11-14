# Opens a throwaway window, puts Sakin's window in front, waits past the grace period,
# checks the test window got calmed, then brings it back and measures how fast it is restored.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class F {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, int p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern uint GetPriorityClass(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct PT { public uint V, C, S; }
  [DllImport("kernel32.dll")] public static extern bool GetProcessInformation(IntPtr h, int c, ref PT i, int s);
  [DllImport("kernel32.dll")] public static extern bool GetProcessInformation(IntPtr h, int c, ref uint i, int s);
  public static void Front(IntPtr h) { keybd_event(0x12,0,0,UIntPtr.Zero); keybd_event(0x12,0,2,UIntPtr.Zero); SetForegroundWindow(h); }
  public static uint Prio(int pid) { IntPtr h = OpenProcess(0x1000, false, pid); uint p = GetPriorityClass(h); CloseHandle(h); return p; }
  public static string State(int pid) {
    IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return "no access";
    uint pr = GetPriorityClass(h);
    var pt = new PT { V = 1 }; GetProcessInformation(h, 4, ref pt, 12);
    uint mem = 0; GetProcessInformation(h, 0, ref mem, 4); CloseHandle(h);
    string p = pr == 0x20 ? "Normal" : pr == 0x4000 ? "BelowNormal" : pr == 0x40 ? "Idle" : pr.ToString("X");
    return "priority=" + p + " efficiency=" + (pt.S == 1 ? "on" : "off") + " memPriority=" + mem;
  }
}
"@
$code = 'Add-Type -AssemblyName System.Windows.Forms; $f = New-Object Windows.Forms.Form; $f.Text = "SakinTestWindow"; [Windows.Forms.Application]::Run($f)'
$t = Start-Process pwsh -ArgumentList "-NoProfile", "-Command", $code -PassThru
for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; $t.Refresh(); if ($t.MainWindowHandle -ne 0) { break } }
if ($t.MainWindowHandle -eq 0) { "test window did not open"; exit 1 }
$sakin = Get-Process Sakin | ? { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
"test window pid $($t.Id) opened:      $([F]::State($t.Id))"
[F]::Front($t.MainWindowHandle); Start-Sleep -Seconds 2
[F]::Front($sakin.MainWindowHandle)
Start-Sleep -Seconds 36
"after 36 s in the background:     $([F]::State($t.Id))"
$sw = [Diagnostics.Stopwatch]::StartNew()
[F]::Front($t.MainWindowHandle)
while ([F]::Prio($t.Id) -ne 0x20 -and $sw.ElapsedMilliseconds -lt 3000) { }
$ms = $sw.ElapsedMilliseconds
"switched back -> restored in $ms ms: $([F]::State($t.Id))"
Stop-Process -Id $t.Id
