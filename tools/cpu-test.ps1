# Foreground speed under a heavy background app, without and with Sakin.
$ErrorActionPreference = "Stop"
if (-not (Test-Path "$PSScriptRoot\bench\out\Hog1.exe")) { & "$PSScriptRoot\bench\build.ps1" | Out-Null }
$root = Split-Path $PSScriptRoot
$benchDir = "$PSScriptRoot\bench\out"
$sakin = "$root\Sakin.exe"
$out = "$root\docs\bench"; New-Item -ItemType Directory -Force $out | Out-Null
$cfg = "$env:LOCALAPPDATA\Sakin\settings.json"
$threads = [Environment]::ProcessorCount
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class C {
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
function Sakin-Off { if (Get-Process Sakin -ErrorAction SilentlyContinue) { & $sakin --quit; Start-Sleep 3 } }
function Sakin-On($rules) {
  Sakin-Off
  $s = Get-Content "$cfg.bak" -Raw | ConvertFrom-Json
  $s.Rules = $rules
  $s | ConvertTo-Json -Depth 5 | Set-Content $cfg
  Start-Process $sakin -ArgumentList "--tray"; Start-Sleep 3
}
function Measure-Jobs($label, $runs = 3) {
  $log = "$out\cpu-$label.csv"; Remove-Item $log -ErrorAction SilentlyContinue
  $times = @()
  for ($r = 0; $r -lt $runs; $r++) {
    # a fresh window each run: launched by the active script it comes to the front by itself
    $n = @(Get-Content $log -ErrorAction SilentlyContinue).Count
    $fg = Start-Win "FgJob" @("fg", "FgJob", "`"$log`"")
    [C]::Front($fg.MainWindowHandle)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (@(Get-Content $log -ErrorAction SilentlyContinue).Count -le $n -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 200 }
    if (@(Get-Content $log -ErrorAction SilentlyContinue).Count -gt $n) { $times += [int]((Get-Content $log | Select-Object -Last 1) -split ",")[2] }
    else { $times += -1 }
    $fg.Kill(); Start-Sleep 1
  }
  $ok = $times | ? { $_ -gt 0 } | Sort-Object
  [pscustomobject]@{ case = $label; runsMs = ($times -join ", "); medianMs = if ($ok) { $ok[[int](($ok.Count - 1) / 2)] } else { "timeout" } }
}
Copy-Item $cfg "$cfg.bak" -Force
$results = @()
try {
  Sakin-Off
  $results += Measure-Jobs "1-idle-machine"

  $spin = Start-Win "Spinner" @("spin", $threads, "Spinner")
  Start-Sleep 2
  $results += Measure-Jobs "2-heavy-background-no-sakin"

  Sakin-On @{}
  Start-Sleep 40          # past the grace period
  $results += Measure-Jobs "3-heavy-background-sakin-default"

  Sakin-On @{ spinner = "calm" }
  Start-Sleep 40
  $results += Measure-Jobs "4-heavy-background-sakin-always-calm-rule"
  $spin.Kill()
}
finally {
  Get-Process Spinner, FgJob, Front -ErrorAction SilentlyContinue | Stop-Process -Force
  Sakin-Off
  Copy-Item "$cfg.bak" $cfg -Force
  Start-Process $sakin -ArgumentList "--tray"
}
$results | Format-Table -AutoSize | Out-String -Width 200 | Tee-Object "$out\cpu-summary.txt"
