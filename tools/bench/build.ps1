# Builds the benchmark test apps into tools\bench\out, one exe name per role
# so Sakin sees each as a separate app.
$ErrorActionPreference = "Stop"
$out = "$PSScriptRoot\out"
dotnet build "$PSScriptRoot\Bench.csproj" -c Release -o $out --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "bench build failed" }
foreach ($n in "Hog1", "Hog2", "Hog3", "Front", "Spinner", "FgJob") { Copy-Item "$out\SakinBench.exe" "$out\$n.exe" -Force }
"bench apps ready in $out"
