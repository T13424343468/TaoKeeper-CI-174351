$t = $null; $e = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile('ci/effect-probe-d1d4.ps1', [ref]$t, [ref]$e)
if ($e.Count) {
  $e | ForEach-Object { 'ERR: ' + $_.Message + ' @line ' + $_.Extent.StartLineNumber }
  exit 1
}
'syntax OK: ' + (Split-Path -Leaf (Get-Item ci/effect-probe-d1d4.ps1))
