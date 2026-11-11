# CI-only: restore original csproj after CI run
param([string]$CsprojPath)
if (Test-Path "$CsprojPath.orig") { Move-Item -Force "$CsprojPath.orig" $CsprojPath; Write-Host "[OK] csproj restored" }
else { Write-Host "[WARN] no backup found" }
