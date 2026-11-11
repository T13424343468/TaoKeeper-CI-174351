# CI-only: hash triple generator (same algorithm as packaging/make-hashes.bat)
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
Push-Location $OutDir
try {
  $files = @(Get-ChildItem -Recurse -File | Where-Object { $_.Name -ne 'hashes.txt' } | Sort-Object FullName)
  $lines = @()
  foreach ($f in $files) {
    $rel = $f.FullName.Substring((Get-Location).Path.Length + 1).Replace('\','/')
    if ($rel -match '[^\x00-\x7F]') { throw ('non-ASCII file name: ' + $rel) }
    $h = (Get-FileHash -Algorithm SHA256 -Path $f.FullName).Hash.ToLower()
    $lines += ($h + '  ' + $rel)
  }
  Set-Content -Path 'hashes.txt' -Value $lines -Encoding ASCII
  Write-Host "[OK] files hashed: $($files.Count)"
} finally { Pop-Location }
