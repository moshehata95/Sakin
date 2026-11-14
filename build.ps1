# Builds the single self-contained Sakin.exe into this folder.
# Usage:  powershell -File build.ps1
$ErrorActionPreference = "Stop"
$env:DOTNET_NOLOGO = 1; $env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$root = $PSScriptRoot
& "$root\tools\make-icon.ps1" | Out-Null
$running = Get-Process Sakin -ErrorAction SilentlyContinue | ? { $_.Path -eq "$root\Sakin.exe" }
if ($running) { & "$root\Sakin.exe" --quit; $running | Wait-Process -Timeout 10 }
dotnet publish "$root\src\Sakin\Sakin.csproj" -c Release -o "$root\.publish" --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Copy-Item "$root\.publish\Sakin.exe" "$root\Sakin.exe" -Force
$mb = [math]::Round((Get-Item "$root\Sakin.exe").Length / 1MB, 1)
"Sakin.exe ready ($mb MB): $root\Sakin.exe"
