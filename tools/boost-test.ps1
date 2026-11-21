param([string]$Sakin = "", [int]$TargetMB = 700, [int]$OtherMB = 700)
# Boost A/B: a "target" app (stands in for Illustrator) holds $TargetMB. Two other apps idle in the
# background. The target goes to the background, then a memory hog pushes the machine past full RAM.
# Phase A: no boost. Phase B: the target is boosted with Ctrl+Alt+B while it's in front.
# Measured: the target's RAM under pressure, how long it takes to re-read its data, its priority,
# whether its working set is pinned, and how much the others gave up.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
$b = "$PSScriptRoot\bench\out"
if (-not $Sakin) { $Sakin = Get-ChildItem "$root\src\Sakin\bin\Debug" -Recurse -Filter Sakin.exe | Select-Object -First 1 -Expand FullName }
$out = "$root\docs\bench"; New-Item -ItemType Directory -Force $out | Out-Null
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, int p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool GetProcessWorkingSetSizeEx(IntPtr h, out IntPtr mn, out IntPtr mx, out uint f);
  [StructLayout(LayoutKind.Sequential)] public struct PT { public uint V, C, S; }
  [DllImport("kernel32.dll")] public static extern bool GetProcessInformation(IntPtr h, int c, ref PT i, int s);
  [StructLayout(LayoutKind.Sequential)] public struct MS { public uint L, Load; public ulong Tot, Av, TP, AP, TV, AV, AE; }
  [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MS m);
  public static double AvailMB() { var m = new MS { L = 64 }; GlobalMemoryStatusEx(ref m); return m.Av / 1048576.0; }
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public static void Front(IntPtr h) {
    for (int i = 0; i < 20 && GetForegroundWindow() != h; i++) { keybd_event(0x12,0,0,UIntPtr.Zero); keybd_event(0x12,0,2,UIntPtr.Zero); SetForegroundWindow(h); System.Threading.Thread.Sleep(150); }
  }
  public static bool IsFront(IntPtr h) { return GetForegroundWindow() == h; }
  public static void CtrlAltB() { keybd_event(0x11,0,0,UIntPtr.Zero); keybd_event(0x12,0,0,UIntPtr.Zero); keybd_event(0x42,0,0,UIntPtr.Zero);
    keybd_event(0x42,0,2,UIntPtr.Zero); keybd_event(0x12,0,2,UIntPtr.Zero); keybd_event(0x11,0,2,UIntPtr.Zero); }
  public static string Pin(int pid) { IntPtr h = OpenProcess(0x1000, false, pid); IntPtr mn, mx; uint f;
    bool ok = GetProcessWorkingSetSizeEx(h, out mn, out mx, out f); CloseHandle(h);
    return ok ? (((f & 1) != 0) ? "pinned " + ((long)mn / 1048576) + " MB" : "not pinned") : "?"; }
  public static string Qos(int pid) { IntPtr h = OpenProcess(0x1000, false, pid); var p = new PT { V = 1 }; GetProcessInformation(h, 4, ref p, 12); CloseHandle(h);
    return p.C == 0 ? "windows decides" : (p.S == 0 ? "never throttled" : "efficiency mode"); }
}
"@
function Win($exe, $a) { $p = Start-Process "$b\$exe.exe" -ArgumentList $a -PassThru; for ($i = 0; $i -lt 120; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }; $p }
function WsMB($name) { [int]((Get-Process $name -ErrorAction SilentlyContinue | Measure-Object WorkingSet64 -Sum).Sum / 1MB) }

function Run($label, [bool]$boost) {
  $log = "$out\boost-$label.csv"; Remove-Item $log -ErrorAction SilentlyContinue
  Get-Process Sakin -ErrorAction SilentlyContinue | % { & $_.Path --quit }; Start-Sleep 3
  Start-Process $Sakin -ArgumentList "--portable", "--tray"; Start-Sleep 4

  $others = "Hog2", "Hog3" | % { Win $_ @("hog", $OtherMB, $_, "`"$log`"") }
  $front = Win "Front" @("hog", "10", "Front", "`"$log`"")
  $target = Win "Hog1" @("hog", $TargetMB, "Hog1", "`"$log`"")
  [K]::Front($target.MainWindowHandle); Start-Sleep 3
  $r = [ordered]@{ case = $label }
  $r.availBefore = [int][K]::AvailMB()
  $r.othersBefore = (WsMB Hog2) + (WsMB Hog3)
  $r.targetInFrontWhenPressed = [K]::IsFront($target.MainWindowHandle)
  if ($boost) { [K]::CtrlAltB(); Start-Sleep 12 }    # the shortcut, pressed while the target is in front
  $target.Refresh()
  $r.targetPriority = $target.PriorityClass
  $r.targetQos = [K]::Qos($target.Id)
  $r.targetPin = [K]::Pin($target.Id)
  $r.othersAfterBoost = (WsMB Hog2) + (WsMB Hog3)
  $r.availAfterBoost = [int][K]::AvailMB()

  # the target goes to the background, then something else floods the RAM
  [K]::Front($front.MainWindowHandle); Start-Sleep 3
  $press = [Math]::Min(6000, [int][K]::AvailMB() + 1500)
  $p = Win "Pressure" @("hog", $press, "Pressure", "`"$out\ignore.csv`"")
  [K]::Front($front.MainWindowHandle)
  $min = 99999
  for ($i = 0; $i -lt 10; $i++) { Start-Sleep 2; $min = [Math]::Min($min, (WsMB Hog1)) }
  $r.pressureMB = $press
  $r.targetRamUnderPressure = $min
  $r.targetRamEnd = WsMB Hog1

  # come back to the target: how long to re-read all of its data
  [K]::Front($target.MainWindowHandle); Start-Sleep 4
  $t = Get-Content $log -ErrorAction SilentlyContinue | ? { $_ -like "Hog1,*" } | Select-Object -Last 1
  $r.targetRereadMs = if ($t) { [int](($t -split ",")[2]) } else { "n/a" }

  if ($boost) { [K]::CtrlAltB(); Start-Sleep 4; $target.Refresh(); $r.priorityAfterStop = $target.PriorityClass; $r.pinAfterStop = [K]::Pin($target.Id) }
  ($others + $front + $target + $p) | % { try { $_.Kill() } catch {} }
  Start-Sleep 3
  [pscustomobject]$r
}

try {
  $a = Run "A-no-boost" $false
  $c = Run "B-boost" $true
}
finally {
  Get-Process Hog1, Hog2, Hog3, Front, Pressure -ErrorAction SilentlyContinue | Stop-Process -Force
  Get-Process Sakin -ErrorAction SilentlyContinue | % { & $_.Path --quit }; Start-Sleep 3
  Start-Process "$env:LOCALAPPDATA\Programs\Sakin\Sakin.exe" -ArgumentList "--tray"
}
$a, $c | Format-List | Out-String -Width 160 | Tee-Object "$out\boost-summary.txt"
