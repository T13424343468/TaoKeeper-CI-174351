# CI-only: activate the commented-out WindowsAppSDK PackageReference in TaoKeeper.App.csproj (pitfall #17)
# v4 (2026-11-12): root cause of MSB4066 - the commented PackageReference sits INSIDE <PropertyGroup>
# (lines 9-26). v1-v3 replaced the comment in place, putting an illegal <PackageReference> inside
# a property group. v4 approach:
#   1) extract the PackageReference element from the comment,
#   2) remove the comment block entirely,
#   3) insert a NEW <ItemGroup> containing that reference immediately before </Project>.
# Fail-fast verification: extracted ref is exactly one; comment opener gone afterwards;
# final XML parses (XDocument.Parse throws on malformed output).
# Restores original file afterwards; never commits changes to src/.
param([string]$CsprojPath)

if (-not (Test-Path $CsprojPath)) {
    Write-Host "[ERROR] csproj not found: $CsprojPath"
    exit 1
}
Copy-Item $CsprojPath "$CsprojPath.orig" -Force
$xml = Get-Content $CsprojPath -Raw

# 1) extract exactly one PackageReference
$refPattern = '<PackageReference\s+Include="Microsoft\.WindowsAppSDK"[^>]*/>'
$refs = [regex]::Matches($xml, $refPattern)
if ($refs.Count -ne 1) {
    Write-Host "[ERROR] expected exactly 1 WindowsAppSDK PackageReference, found $($refs.Count) - failing fast"
    exit 1
}
$ref = $refs[0].Value

# 2) remove the pitfall comment block entirely
$commentPattern = '(?s)<!--\s*真机构建前取消下行注释.*?-->'
if ($xml -notmatch $commentPattern) {
    Write-Host "[ERROR] WindowsAppSDK comment block not found - failing fast"
    exit 1
}
$xml = [regex]::Replace($xml, $commentPattern, '')

# 3) insert a new ItemGroup with the reference before </Project>
$closeIdx = $xml.LastIndexOf('</Project>')
if ($closeIdx -lt 0) {
    Write-Host "[ERROR] </Project> not found - failing fast"
    exit 1
}
$itemGroup = "  <ItemGroup>`r`n    $ref`r`n  </ItemGroup>`r`n"
$xml = $xml.Substring(0, $closeIdx) + $itemGroup + $xml.Substring($closeIdx)

# verify: comment opener gone + final XML is well-formed
if ($xml -match '真机构建前取消下行注释') {
    Write-Host "[ERROR] verification failed: comment opener still present after substitution"
    exit 1
}
try {
    $null = [System.Xml.Linq.XDocument]::Parse($xml)
} catch {
    Write-Host "[ERROR] verification failed: resulting csproj is not well-formed XML - $($_.Exception.Message)"
    exit 1
}

Set-Content -Path $CsprojPath -Value $xml -Encoding UTF8 -NoNewline
Write-Host "[OK] WindowsAppSDK reference moved to new ItemGroup before </Project>, XML verified (backup at .orig)"
