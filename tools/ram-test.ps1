param([int]$HogMB = 700, [int]$Minutes = 4, [string]$Mode = "strong")
# A/B test: three background apps holding $HogMB each, measured without Sakin, then with it.
$ErrorActionPreference = "Stop"
if (-not (Test-Path "$PSScriptRoot\bench\out\Hog1.exe")) { & "$PSScriptRoot\bench\build.ps1" | Out-Null }
$root = Split-Path $PSScriptRoot
$benchDir = "$PSScriptRoot\bench\out"
$sakin = "$root\Sakin.exe"
$out = "$root\docs\bench"; New-Item -ItemType Directory -Force $out | Out-Null
$cfg = "$env:LOCALAPPDATA\Sakin\settings.json"
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class M {
  [StructLayout(LayoutKind.Sequential)] public struct MS { public uint L, Load; public ulong Tot, Av, TP, AP, TV, AV, AE; }
  [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MS m);
  public static double AvailMB() { var m = new MS { L = 64 }; GlobalMemoryStatusEx(ref m); return m.Av / 1048576.0; }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  public static void Front(IntPtr h) { keybd_event(0x12,0,0,UIntPtr.Zero); keybd_event(0x12,0,2,UIntPtr.Zero); SetForegroundWindow(h); }
}
"@

function Start-Win($exe, $argsList) {
  $p = Start-Process "$benchDir\$exe.exe" -ArgumentList $argsList -PassThru
  for ($i = 0; $i -lt 120; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
  return $p
}

function Run-Phase([string]$name, [bool]$withSakin) {
  $log = "$out\$name-events.csv"; Remove-Item $log -ErrorAction SilentlyContinue
  if (Get-Process Sakin -ErrorAction SilentlyContinue) { & $sakin --quit; Start-Sleep 3 }
  if ($withSakin) { Start-Process $sakin -ArgumentList "--tray"; Start-Sleep 3 }

  $hogs = 1..3 | % { Start-Win "Hog$_" @("hog", $HogMB, "Hog$_", "`"$log`"") }
  $front = Start-Win "Front" @("hog", "10", "Front", "`"$log`"")
  foreach ($h in $hogs) { [M]::Front($h.MainWindowHandle); Start-Sleep -Milliseconds 400 }   # each was "used" once
  [M]::Front($front.MainWindowHandle)
  $t0 = Get-Date
  $rows = @()
  while (((Get-Date) - $t0).TotalSeconds -lt $Minutes * 60) {
    $ws = (Get-Process Hog1, Hog2, Hog3 -ErrorAction SilentlyContinue | Measure-Object WorkingSet64 -Sum).Sum
    $pi = (Get-Counter '\Memory\Pages Input/sec' -SampleInterval 1 -MaxSamples 1).CounterSamples[0].CookedValue
    $rows += [pscustomobject]@{ sec = [int]((Get-Date) - $t0).TotalSeconds; availMB = [int][M]::AvailMB(); hogsWsMB = [int]($ws / 1MB); pagesInPerSec = [int]$pi; hog1Prio = (Get-Process Hog1 -ErrorAction SilentlyContinue).PriorityClass }
    Start-Sleep -Seconds 4
  }
  $rows | Export-Csv "$out\$name-samples.csv" -NoTypeInformation
  # come back to one of them and time getting its memory back
  $before = $hogs[0].PriorityClass
  [M]::Front($hogs[0].MainWindowHandle); Start-Sleep -Seconds 4
  $hogs[0].Refresh(); $after = $hogs[0].PriorityClass
  $touch = (Get-Content $log -ErrorAction SilentlyContinue | ? { $_ -like "Hog1,*" } | Select-Object -Last 1)
  $first = $rows[0]; $last = $rows[-1]
  $res = [pscustomobject]@{
    phase = $name
    availStartMB = $first.availMB; availEndMB = $last.availMB
    hogsStartMB = $first.hogsWsMB; hogsEndMB = $last.hogsWsMB
    hog1PrioBeforeSwitch = $before; hog1PrioAfterSwitch = $after
    switchBackTouchMs = if ($touch) { ($touch -split ",")[2] } else { "n/a" }
  }
  ($hogs + $front) | % { try { $_.Kill() } catch {} }
  Start-Sleep 2
  return $res
}

Copy-Item $cfg "$cfg.bak" -Force
try {
  $a = Run-Phase "A-without-sakin" $false
  $s = Get-Content $cfg -Raw | ConvertFrom-Json
  $s.Mode = $Mode
  $p = @{ light = @(0,60,20,10,8,20); balanced = @(1,30,10,20,6,15); strong = @(2,10,3,35,10,25) }[$Mode]
  $s.Level = $p[0]; $s.GraceSec = $p[1]; $s.DormantMin = $p[2]; $s.TrimBelowPct = $p[3]; $s.BusyCpuPct = $p[4]; $s.BusyGpuPct = $p[5]
  $s | ConvertTo-Json -Depth 5 | Set-Content $cfg
  $b = Run-Phase "B-with-sakin-$Mode" $true
}
finally {
  if (Get-Process Sakin -ErrorAction SilentlyContinue) { & $sakin --quit; Start-Sleep 3 }
  Copy-Item "$cfg.bak" $cfg -Force
  Start-Process $sakin -ArgumentList "--tray"
}
$a, $b | Format-List | Out-String | Tee-Object "$out\ram-summary.txt"
