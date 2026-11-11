# CI-only: uncomment WindowsAppSDK PackageReference in TaoKeeper.App.csproj (pitfall #17)
# Restores original file afterwards; never commits changes to src/.
param([string]$CsprojPath, [string]$RepoRoot)
Copy-Item $CsprojPath "$CsprojPath.orig" -Force
$lines = Get-Content $CsprojPath -Raw
$lines = $lines -replace '(?m)^(\s*)<!-- (\s*<PackageReference Include="Microsoft\.WindowsAppSDK"[^\r\n]*) -->', '$1$2'
Set-Content -Path $CsprojPath -Value $lines -Encoding UTF8 -NoNewline
Write-Host "[OK] WindowsAppSDK reference uncommented (backup at .orig)"
