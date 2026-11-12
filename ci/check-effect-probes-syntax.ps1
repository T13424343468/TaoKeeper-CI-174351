# Parse-check the D-2/D-3/D-5 effect probes (same gate as check-syntax.ps1).
$t = $null; $e = $null
$exit = 0
foreach ($f in @('ci/effect-probe-d2-pagefile.ps1','ci/effect-probe-d3-checkup.ps1','ci/effect-probe-d5-startup.ps1')) {
  $t = $null; $e = $null
  [System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$t, [ref]$e) | Out-Null
  if ($e.Count) {
    $e | ForEach-Object { 'ERR: ' + $_.Message + ' @' + (Split-Path -Leaf $f) + ':' + $_.Extent.StartLineNumber }
    $exit = 1
  } else {
    'syntax OK: ' + (Split-Path -Leaf $f)
  }
}
exit $exit
