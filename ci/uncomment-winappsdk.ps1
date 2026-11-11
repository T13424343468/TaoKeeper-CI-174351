# CI-only: uncomment WindowsAppSDK PackageReference in TaoKeeper.App.csproj (pitfall #17)
# v2 (2026-11-12): csproj uses a two-line comment "<!-- 真机...：\n <PackageReference .../> -->";
# the single-line regex in v1 never matched, leaving SDK refs commented (114 CS0246/CS0234 errors).
# Restores original file afterwards; never commits changes to src/.
param([string]$CsprojPath, [string]$RepoRoot)
Copy-Item $CsprojPath "$CsprojPath.orig" -Force
$lines = Get-Content $CsprojPath -Raw
$pattern = '(?s)<!--\s*真机构建前取消下行注释[^>]*?：\s*(<PackageReference\s+Include="Microsoft\.WindowsAppSDK"[^>]*/>)\s*-->'
if ($lines -notmatch $pattern) {
    Write-Host "[ERROR] WindowsAppSDK comment pattern not found - failing fast, uncommented ref would be wrong"
    exit 1
}
$lines = $lines -replace $pattern, '$1'
Set-Content -Path $CsprojPath -Value $lines -Encoding UTF8 -NoNewline
if ((Select-String -Path $CsprojPath -Pattern '<PackageReference Include="Microsoft.WindowsAppSDK"' -NotMatch '^\s*<!--' | Measure-Object).Count -lt 1) {
    Write-Host "[ERROR] verification failed: PackageReference still commented"
    exit 1
}
Write-Host "[OK] WindowsAppSDK reference uncommented and verified (backup at .orig)"
