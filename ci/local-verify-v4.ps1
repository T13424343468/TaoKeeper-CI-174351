# Local sanity check: interop_abi.h macro placement + uncomment-winappsdk.ps1 v4 on a csproj copy
$ErrorActionPreference = 'Stop'
$repo = "/root/workspace/projects/575558/TaoKeeper-Package-v1"

# --- check 1: all 11 declarations start with TK_INTEROP_API ---
$content = Get-Content "$repo/native/TaoKeeperScan/include/tk/interop_abi.h" -Raw
$decls = [regex]::Matches($content, '(?m)^TK_INTEROP_API ')
$bad = [regex]::Matches($content, '(?m)^(void\*|void|int|size_t|uint64_t) TK_INTEROP_API ')
Write-Host "DECLS_WITH_MACRO_PREFIX=$($decls.Count) DECLS_WITH_MACRO_SUFFIX=$($bad.Count)"
if ($decls.Count -ne 11 -or $bad.Count -ne 0) { Write-Host "[FAIL] header macro placement"; exit 1 }

# --- check 2: run v4 script on a copy of the csproj and build ---
$tmp = "/tmp/csproj-test-v4"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory $tmp | Out-Null
Copy-Item "$repo/src" "$tmp/src" -Recurse
$csproj = "$tmp/src/TaoKeeper.App/TaoKeeper.App.csproj"
pwsh -NoProfile -File "$repo/ci/uncomment-winappsdk.ps1" -CsprojPath $csproj
if ($LASTEXITCODE -ne 0) { Write-Host "[FAIL] uncomment script"; exit 1 }

# show the tail of the patched csproj
Write-Host "--- patched csproj tail ---"
Get-Content $csproj | Select-Object -Last 12 | ForEach-Object { Write-Host $_ }
Write-Host "--- check: no PackageReference inside PropertyGroup ---"
$xmlText = Get-Content $csproj -Raw
$pgEnd = $xmlText.IndexOf('</PropertyGroup>')
$pgSeg = $xmlText.Substring(0, $pgEnd)
if ($pgSeg -match '<PackageReference') { Write-Host "[FAIL] PackageReference still inside PropertyGroup"; exit 1 }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet build "$tmp/src/TaoKeeper.App/TaoKeeper.App.csproj" -c Release -v q --nologo 2>&1 | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { Write-Host "[FAIL] dotnet build exited $LASTEXITCODE"; exit 1 }
Write-Host "[PASS] local v4 verification"
