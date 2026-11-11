# CI-only: uncomment WindowsAppSDK PackageReference in TaoKeeper.App.csproj (pitfall #17)
# v3 (2026-11-12): v2 verification used Select-String -NotMatch with a string value (invalid);
# replaced with a fail-fast check that the comment opener is gone after substitution.
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
if ($lines -match '真机构建前取消下行注释') {
    Write-Host "[ERROR] verification failed: comment opener still present after substitution"
    exit 1
}
Write-Host "[OK] WindowsAppSDK reference uncommented and verified (backup at .orig)"
