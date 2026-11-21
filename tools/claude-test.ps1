# Claude mode, end to end on this PC.
#   * apps started outside Claude go to sleep (Idle priority + Efficiency mode, RAM handed back)
#   * an app Claude starts runs normally
#   * an app with a live local connection from Claude wakes up, then sleeps again after the linger time
#   * a listed "Claude app" wakes when it starts working
#   * the power plan switches (no sleep, lid ignored, no turbo) and comes back exactly
# Run it from a Claude Code session: this shell is a Claude process, so what it starts counts as Claude's.
param([int]$LingerMin = 1)
$ErrorActionPreference = "Stop"
$root  = Split-Path $PSScriptRoot
$bench = "$PSScriptRoot\bench\out"
$sakin = "$root\Sakin.exe"
$out   = "$root\docs\bench"
New-Item -ItemType Directory -Force $out | Out-Null
$log = "$out\claude-test.txt"
Remove-Item $log -ErrorAction SilentlyContinue
function Say($m) { $l = "$(Get-Date -Format HH:mm:ss)  $m"; Write-Host $l; Add-Content $log $l }

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Q {
  [StructLayout(LayoutKind.Sequential)] public struct PT { public uint V, C, S; }
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, int p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool GetProcessInformation(IntPtr h, int c, ref PT i, int s);
  public static string Eco(int pid) {
    IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return "?";
    var t = new PT { V = 1 }; bool ok = GetProcessInformation(h, 4, ref t, 12); CloseHandle(h);
    if (!ok) return "?"; return (t.C & 1) == 0 ? "windows" : (t.S & 1) != 0 ? "eco" : "fast";
  }
}
"@

function St($name) {
    $p = Get-Process $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$p) { return "${name}: not running" }
    "{0,-7} prio={1,-12} power={2,-8} ram={3,5} MB" -f $name, $p.PriorityClass, [Q]::Eco($p.Id), [int]($p.WorkingSet64 / 1MB)
}
function Plan { (powercfg /getactivescheme) -replace '.*\((.*)\).*', '$1' }
function PlanValue($sub, $set) {
    $q = powercfg /qh SCHEME_CURRENT $sub $set
    $m = $q | Select-String "Current AC Power Setting Index: 0x([0-9a-f]+)"
    if (!$m) { return "?" }
    [Convert]::ToInt32($m.Matches[0].Groups[1].Value, 16)
}
function StartOutside($cmd) {
    # started by WMI, not by this shell, so it is not a Claude process
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
    $r.ProcessId
}

# ---------------- setup ----------------
$settingsPath = "$env:LOCALAPPDATA\Sakin\settings.json"
Copy-Item $settingsPath "$out\settings.backup.json" -Force
$installed = "$env:LOCALAPPDATA\Programs\Sakin\Sakin.exe"
if (Test-Path $installed) { & $installed --quit; Start-Sleep 3 }
Get-Process Sakin -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue

$s = Get-Content $settingsPath -Raw | ConvertFrom-Json
$s | Add-Member -Force ClaudeMode $false
$s | Add-Member -Force ClaudeLingerMin $LingerMin
$apps = [System.Collections.Generic.List[string]]::new(); if ($s.PSObject.Properties["ClaudeApps"] -and $s.ClaudeApps) { $s.ClaudeApps | ForEach-Object { $apps.Add($_) } }
if ($apps.Count -eq 0) { "chrome","illustrator","photoshop","adobe premiere pro","afterfx","powerpnt","winword","excel" | ForEach-Object { $apps.Add($_) } }
if (!$apps.Contains("driven")) { $apps.Add("driven") }
$s | Add-Member -Force ClaudeApps $apps
$s | ConvertTo-Json -Depth 5 | Set-Content $settingsPath -Encoding UTF8

$planBefore = Plan
Say "power plan before: $planBefore  (turbo mode=$(PlanValue SUB_PROCESSOR PERFBOOSTMODE), sleep after=$(PlanValue SUB_SLEEP STANDBYIDLE)s, lid=$(PlanValue SUB_BUTTONS LIDACTION))"

Start-Process $sakin "--portable --tray"
Start-Sleep 4
StartOutside "`"$bench\Hog1.exe`" hog 700 Hog1 $env:TEMP\sakin-ct.csv" | Out-Null
StartOutside "`"$bench\Bridge.exe`" serve 47001 Bridge" | Out-Null
StartOutside "`"$bench\Driven.exe`" serve 47002 Driven" | Out-Null
Start-Process "$bench\Hog3.exe" "hog 300 Hog3 $env:TEMP\sakin-ct.csv"     # started by Claude (this shell)
Start-Sleep 10
Say "--- before Claude mode"
"Hog1", "Bridge", "Driven", "Hog3" | ForEach-Object { Say (St $_) }
$free0 = [int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024)
Say "free RAM: $free0 MB"

# ---------------- on ----------------
& $sakin --claude-mode on
Start-Sleep 20
Say "--- Claude mode on (20 s)"
Say "power plan: $(Plan)  turbo mode=$(PlanValue SUB_PROCESSOR PERFBOOSTMODE)  sleep after=$(PlanValue SUB_SLEEP STANDBYIDLE)s  lid=$(PlanValue SUB_BUTTONS LIDACTION)  screen off=$(PlanValue SUB_VIDEO VIDEOIDLE)s"
"Hog1", "Bridge", "Driven", "Hog3" | ForEach-Object { Say (St $_) }
Start-Sleep 40
$free1 = [int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024)
Say "after 60 s: free RAM $free1 MB (was $free0)"
"Hog1", "Bridge", "Driven" | ForEach-Object { Say (St $_) }

# ---------------- Claude connects to Bridge ----------------
Say "--- Claude opens a local connection to Bridge and asks it for work"
$tcp = [System.Net.Sockets.TcpClient]::new("127.0.0.1", 47001)
$ns = $tcp.GetStream()
$t0 = Get-Date
$woke = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process Bridge
    if ($p.PriorityClass -eq "Normal" -and [Q]::Eco($p.Id) -ne "eco") { $woke = ((Get-Date) - $t0).TotalSeconds; break }
    Start-Sleep -Milliseconds 500
}
Say ("Bridge woke after {0:0.0} s" -f $woke)
$sw = [Diagnostics.Stopwatch]::StartNew(); $ns.WriteByte(1); $buf = New-Object byte[] 3; [void]$ns.Read($buf, 0, 3)
Say ("one request to Bridge answered in {0} ms (1500 ms of work)" -f $sw.ElapsedMilliseconds)
Say (St "Bridge")

# ---------------- something else drives Driven (a listed Claude app) ----------------
Say "--- Driven (a listed Claude app) gets work from outside Claude"
$drv = "`$c=[Net.Sockets.TcpClient]::new('127.0.0.1',47002);`$n=`$c.GetStream();`$b=New-Object byte[] 3;1..12|%{`$n.WriteByte(1);[void]`$n.Read(`$b,0,3)}"
StartOutside "powershell -NoProfile -WindowStyle Hidden -Command $drv" | Out-Null
$woke2 = $null; $t0 = Get-Date
for ($i = 0; $i -lt 40; $i++) {
    $p = Get-Process Driven
    if ($p.PriorityClass -eq "Normal" -and [Q]::Eco($p.Id) -ne "eco") { $woke2 = ((Get-Date) - $t0).TotalSeconds; break }
    Start-Sleep -Milliseconds 500
}
Say ("Driven woke after {0:0.0} s" -f $woke2)
Say (St "Driven"); Say (St "Hog1")

# ---------------- Claude lets go ----------------
$tcp.Dispose()
Say "--- Claude closed the connection; waiting for the $LingerMin min linger"
Start-Sleep ($LingerMin * 60 + 45)
"Bridge", "Driven", "Hog1", "Hog3" | ForEach-Object { Say (St $_) }

# ---------------- off ----------------
& $sakin --claude-mode off
Start-Sleep 6
Say "--- Claude mode off"
Say "power plan: $(Plan)  turbo mode=$(PlanValue SUB_PROCESSOR PERFBOOSTMODE)  sleep after=$(PlanValue SUB_SLEEP STANDBYIDLE)s  lid=$(PlanValue SUB_BUTTONS LIDACTION)"
Say ("plan restored: {0}" -f ((Plan) -eq $planBefore))
Say ("Sakin plan left behind: {0}" -f [bool](powercfg /list | Select-String "Sakin"))
"Hog1", "Bridge", "Driven", "Hog3" | ForEach-Object { Say (St $_) }

# ---------------- cleanup ----------------
"Hog1", "Bridge", "Driven", "Hog3" | ForEach-Object { Get-Process $_ -ErrorAction SilentlyContinue | Stop-Process -Force }
& $sakin --quit; Start-Sleep 3
Copy-Item "$out\settings.backup.json" $settingsPath -Force
if (Test-Path $installed) { Start-Process $installed "--tray" }
Say "done"
