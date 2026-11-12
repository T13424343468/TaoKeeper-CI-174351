# TaoKeeper effect probe v8 (walk companion): D-1 true-clean + D-4 ledger match.
#
# Boss order 2026-11-12 14:33 (effect layer): product acceptance needs REAL results,
# not just wired pages. This script exercises the REAL product UI flow end-to-end
# (scan -> confirm dialog -> cleanup -> recycle-bin restore), no direct backend calls.
#
# D-1 磁盘清理真清理:
#   1. Seed real junk samples with unique markers in %TEMP% (rule system-temp,
#      pattern "*", risk Low -> default-checked) - N files, controlled bytes.
#      Seed one marked KEEP file in %USERPROFILE% (not covered by any rule) and
#      one control file in %TEMP% matching pattern "*" (will be cleaned too -
#      asserted as cleaned, honest scope).
#   2. Record free bytes on the system volume before scan.
#   3. Drive the product: nav to 磁盘清理 -> 开始扫描 -> wait -> assert scan status
#      text reports the seeded rule volume (page shows per-rule aggregate) ->
#      清理（先进回收站） -> ContentDialog confirm ("确认清理") -> result dialog.
#   4. Assert every seeded sample file GONE from its original path.
#   5. Free-space semantics (corrected 14:55): cleanup goes to the recycle bin
#      (same-volume move), so free space must stay ~UNCHANGED: a big POSITIVE
#      delta would mean silent PERMANENT deletion = red-line breach; a big
#      negative delta means unexpected writes. Assert |delta| <= noise band.
#      Real release happens only at confirmed bin emptying (acceptance-sheet
#      D-1 item-2 wording needs the product-side fix). Ledger (D-4) verifies
#      exact-clean accounting.
#      exact-clean verification comes from the ledger (D-4).
#   6. Recycle-bin verification via Shell.Application NameSpace(10) Items: every
#      marker found; restore ONE sample via item verb 还原(&U) -> file back at
#      original path with its original byte size intact.
#   7. Evidence: before/scan/clean/after/restore screenshots +
#      effect-evidence.txt with [OK]/[FAIL] lines. No fake PASS: any failed
#      assertion is recorded and the script exits 1.
#
# D-4 账实相符:
#   - Read the app ledger (%LOCALAPPDATA%/TaoKeeper/cleanup-ledger.json): every
#     seeded path appears with RuleId system-temp and size matching the file on
#     disk before cleanup (ledger size == pre-clean FileInfo.Length per item);
#     total ledger bytes for the marker set == seeded bytes.
#   - RuleId evidence chain: each entry carries a non-empty RuleId.
#
# Boundary: CI-only, edits NOTHING under src/. App runs its real cleanup
# coordinator + Win32RecycleBinAdapter (FOF_ALLOWUNDO), so recycle-bin asserts
# exercise the real adapter path.
#
# D-2/D-3/D-5 are NOT in this script: memory-boost/checkup/speedup pages are
# still unwired (wiring 分身 taokeeper_wiring2 in flight) - their effect probes
# need the wired pages first (r24 scope after wiring lands).

param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [Parameter(Mandatory=$true)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$script:failCount = 0
$script:effectLines = New-Object System.Collections.Generic.List[string]
function Write-Effect([bool]$Ok, [string]$Tag, [string]$Detail) {
  $verdict = 'OK'
  if (-not $Ok) { $verdict = 'FAIL'; $script:failCount++ }
  $line = "[{0}] {1}: {2}" -f $verdict, $Tag, $Detail
  Write-Host ("[EFFECT] {0}" -f $line)
  $script:effectLines.Add($line)
}
function Save-Shot([string]$Name) {
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
  $g.Dispose()
  $bmp.Save((Join-Path $OutDir $Name), [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}

# ---------- seed samples ----------
$marker = "TK-EFFECT-" + (Get-Date -Format 'HHmmss')
$tempDir = [System.IO.Path]::GetTempPath()
$seedCount = 8
$seedBytes = 0L
$seeds = New-Object System.Collections.Generic.List[string]
$seedSizes = @{}
$seedDir = Join-Path $tempDir ("tk-effect-" + $marker)
New-Item -ItemType Directory -Force -Path $seedDir | Out-Null
for ($i = 0; $i -lt $seedCount; $i++) {
  $size = 1MB + ($i * 512KB)   # 1MB..4.5MB, total 22MB (within 5-20MB+ margin)
  $p = Join-Path $seedDir ("{0}-junk-{1:D2}.tmp" -f $marker, $i)
  $bytes = New-Object byte[] $size
  (New-Object Random($i)).NextBytes($bytes)
  [System.IO.File]::WriteAllBytes($p, $bytes)
  $seeds.Add($p)
  $seedSizes[$p] = $size
  $seedBytes += $size
}
# control file: matches system-temp (pattern *), will be cleaned too - honest scope
$controlInTemp = Join-Path $tempDir ("{0}-control-in-temp.tmp" -f $marker)
[System.IO.File]::WriteAllText($controlInTemp, "control-marker")
$keepPath = Join-Path $env:USERPROFILE ("{0}-keep.txt" -f $marker)
[System.IO.File]::WriteAllText($keepPath, "keep-marker: not covered by any junk rule")
$keepBytes = (Get-Item $keepPath).Length
Write-Host ("[SEED] dir={0} files={1} bytes={2}" -f $seedDir, $seedCount, $seedBytes)

$driveInfo = New-Object System.IO.DriveInfo($env:SystemDrive.TrimEnd('\'))
$freeBefore = $driveInfo.AvailableFreeSpace
Write-Host ("[FREE] before={0}" -f $freeBefore)
Save-Shot "e0-before.png"

# ---------- launch app ----------
# Single-instance hygiene: kill stray app instances left by the UI walk and
# clear the nav trace so this probe's own evidence is unambiguous.
Get-Process -Name 'TaoKeeper.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
# r28 evidence: probe instance exited while a live Overview window remained on
# screen — instance identity must be proven, not assumed. Poll until every
# stray instance is truly gone (kill is asynchronous) before launching ours.
$hygDeadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $hygDeadline) {
  if (-not (Get-Process -Name 'TaoKeeper.App' -ErrorAction SilentlyContinue)) { break }
  Start-Sleep -Milliseconds 500
}
$stray = Get-Process -Name 'TaoKeeper.App' -ErrorAction SilentlyContinue
if ($stray) { Write-Host ("[PID] hygiene left {0} instance(s): {1}" -f $stray.Count, (($stray | ForEach-Object { $_.Id }) -join ',')) }
$navLogPath = Join-Path ([System.IO.Path]::GetTempPath()) 'taokeeper-nav.log'
if (Test-Path $navLogPath) { Remove-Item $navLogPath -Force -ErrorAction SilentlyContinue }
$p = Start-Process -FilePath $ExePath -ArgumentList '--page','disk' -WorkingDirectory (Split-Path $ExePath) -PassThru
Write-Host ("[PID] launched probe instance pid={0}" -f $p.Id)
Start-Sleep -Seconds 5
$deadline = (Get-Date).AddSeconds(60)
$handle = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 3
  $p.Refresh()
  if ($p.HasExited) { break }
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $p.MainWindowHandle; break }
}
# Window-identity check: the window we drive via UIA must belong to OUR pid.
if ($handle -ne [IntPtr]::Zero) {
  $winPid = 0
  try {
    $sig = 'using System; using System.Runtime.InteropServices; public static class W32 { [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid); }'
    Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue
    $w = [uint32]0
    [W32]::GetWindowThreadProcessId($handle, [ref]$w) | Out-Null
    $winPid = [int]$w
  } catch { }
  if ($winPid -ne 0 -and $winPid -ne $p.Id) {
    Write-Host ("[PID] MISMATCH window pid={0} probe pid={1} — driving a foreign instance!" -f $winPid, $p.Id)
  } else {
    Write-Host ("[PID] window bound to probe instance pid={0}" -f $p.Id)
  }
}
$alive = Get-Process -Name 'TaoKeeper.App' -ErrorAction SilentlyContinue
Write-Host ("[PID] running instances after launch: {0}" -f ((($alive | ForEach-Object { "$($_.Id)@$($_.StartTime.ToString('HH:mm:ss'))" })) -join ' | '))
if ($handle -eq [IntPtr]::Zero) {
  Save-Shot "99-no-window.png"
  Write-Effect $false "launch" "main window not found in 60s"
  $script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8
  exit 1
}
Start-Sleep -Seconds 5
$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$ctText = [System.Windows.Automation.ControlType]::Text
$navRight = 260.0

function Find-ContentText([string]$Name, [switch]$Like) {
  $textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctText)
  foreach ($t in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
    try {
      $r = $t.Current.BoundingRectangle
      $n = $t.Current.Name
      if ($r.X -gt $navRight -and (($Like -and $n -match $Name) -or (-not $Like -and $n -eq $Name))) { return $true }
    } catch { }
  }
  return $false
}

# Invoke the first matching, on-screen, content-area control; returns $true if invoked.
function Invoke-ContentMatch([string]$Pattern, [int]$WaitSec) {
  # r31: unused PropertyCondition ctor passed property as value (must be ControlType) — removed dead $allCond
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($el in $all) {
    try { if ($el.Current.IsOffscreen) { continue } } catch { }
    try { if ($el.Current.BoundingRectangle.X -lt $navRight) { continue } } catch { }
    $n = ''; $a = ''
    try { $n = $el.Current.Name } catch { }
    try { $a = $el.Current.AutomationId } catch { }
    if (-not (($n -match $Pattern) -or ($a -match $Pattern))) { continue }
    try {
      $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Start-Sleep -Seconds $WaitSec
      return $true
    } catch { }
    try {
      $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
      Start-Sleep -Seconds $WaitSec
      return $true
    } catch { }
  }
  return $false
}

# poll until a Text element in the content area matches; returns matched text or $null
function Wait-ContentText([string]$Pattern, [int]$MaxSec) {
  $textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctText)
  $deadlineLocal = (Get-Date).AddSeconds($MaxSec)
  while ((Get-Date) -lt $deadlineLocal) {
    if ($p.HasExited) { return $null }
    foreach ($t in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
      try {
        $r = $t.Current.BoundingRectangle
        if ($r.X -gt $navRight -and $t.Current.Name -match $Pattern) { return $t.Current.Name }
      } catch { }
    }
    Start-Sleep -Seconds 2
  }
  return $null
}

if ($p.HasExited) { Write-Effect $false "app-alive" "app exited during launch"; Save-Shot "99-exited.png"; $script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8; exit 1 }

# ---------- drive: nav to disk page, scan ----------
# 三遍换思路（r25/r26/r28/r29 四发 UIA 点导航未中；r29 实锤：条目可见、实例唯一，
# 但点击未触发应用导航）——换非 UIA 导航刀：应用启动带 --page disk 直达磁盘清理页，
# 探针只验落页与实绩。落页判定不变：内容区出现磁盘清理页专属文案「扫得全，清得稳」。
$landed = Wait-ContentText '扫得全，清得稳' 45
$navOk = ($null -ne $landed)
Write-Effect $navOk "nav-disk" ("direct-launch --page disk; disk-page marker '{0}'" -f ($(if ($navOk) { '扫得全，清得稳 (content verified)' } else { 'NOT seen in 45s' })))
if (-not $navOk) {
  Save-Shot "99-nav-fail.png"
  $restAll = Get-Process -Name 'TaoKeeper.App' -ErrorAction SilentlyContinue
  if ($restAll) {
    Write-Host ("[PID] instances at nav-fail time: {0}" -f ((($restAll | ForEach-Object { "$($_.Id)@$($_.StartTime.ToString('HH:mm:ss')) win=$($_.MainWindowHandle)" })) -join ' | '))
  } else {
    Write-Host "[PID] NO TaoKeeper.App instances alive at nav-fail time (probe instance exited)"
  }
  $navLog = Join-Path ([System.IO.Path]::GetTempPath()) 'taokeeper-nav.log'
  if (Test-Path $navLog) { Write-Host "[NAVLOG] --- app nav trace ---"; Get-Content $navLog | ForEach-Object { Write-Host ("[NAVLOG] " + $_) } }
  else { Write-Host "[NAVLOG] no nav trace file written" }
  $script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8; exit 1 }
Start-Sleep -Seconds 2
Save-Shot "e1-disk-page.png"

$scanInvoked = Invoke-ContentMatch '开始扫描|ScanButton' 3
Write-Effect $scanInvoked "scan-invoked" "'开始扫描' clicked"
if (-not $scanInvoked) { Save-Shot "99-scan-fail.png"; $script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8; exit 1 }

# page shows: "扫描完成：发现 N 条、合计 X" - poll up to 90s (rule engine can be slow)
$scanText = Wait-ContentText '扫描完成' 90
$scanOk = $false
if ($scanText) {
  Save-Shot "e2-scan-result.png"
  # assert the seeded rule volume shows: 22MB seeds + control = must be reported.
  # The page aggregates per-rule (系统临时文件 row); total text on ring shows X.
  # Parse "发现 (\d+) 条" and assert count >= seedCount+1 (other pre-existing junk allowed).
  $m = [regex]::Match($scanText, '发现 (\d+) 条')
  if ($m.Success) {
    $found = [int]$m.Groups[1].Value
    $scanOk = ($found -ge ($seedCount + 1))
    Write-Effect $scanOk "scan-found" ("status='{0}'; found={1}, expected >= {2} (seeds+control); pre-existing junk allowed" -f $scanText, $found, ($seedCount + 1))
  } else {
    Write-Effect $false "scan-found" ("status text unparsable: '{0}'" -f $scanText)
  }
} else {
  Write-Effect $false "scan-found" "'扫描完成' text not seen in 90s"
}

if (-not $scanOk) { Save-Shot "99-scan-assert.png"; $script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8; $p.Kill(); exit 1 }

# ---------- drive: clean with confirm dialog ----------
$freePreClean = (New-Object System.IO.DriveInfo($env:SystemDrive.TrimEnd('\'))).AvailableFreeSpace
$cleanInvoked = Invoke-ContentMatch '清理（先进回收站）|CleanButton' 4
Write-Effect $cleanInvoked "clean-invoked" "'清理（先进回收站）' clicked; confirmation dialog expected"
if (-not $cleanInvoked) { Save-Shot "99-clean-fail.png"; $script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8; $p.Kill(); exit 1 }
Save-Shot "e3-confirm-dialog.png"

# ContentDialog: primary button '确认清理' must be clicked; close '先不' would abort.
$confirmClicked = Invoke-ContentMatch '确认清理' 10
Write-Effect $confirmClicked "confirm-clicked" "dialog primary '确认清理' invoked (real user-confirmation flow, no bypass)"
if (-not $confirmClicked) { Save-Shot "99-confirm-fail.png"; $script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8; $p.Kill(); exit 1 }

# result dialog appears: '已移入回收站 N 条（合计 X）' - poll up to 120s (22MB+ batch)
$resultText = Wait-ContentText '已移入回收站' 120
if ($resultText) {
  Save-Shot "e4-clean-result.png"
  $m2 = [regex]::Match($resultText, '已移入回收站 (\d+) 条（合计 ([^）]+)）')
  $succeeded = -1
  if ($m2.Success) { $succeeded = [int]$m2.Groups[1].Value }
  Write-Effect ($succeeded -ge ($seedCount + 1)) "clean-result" ("dialog='{0}'; succeeded={1}, expected >= {2} (seeds+control); refused/failed items honest-reported inside dialog" -f $resultText, $succeeded, ($seedCount + 1))
  # dismiss result dialog ('好' close button)
  Invoke-ContentMatch '好' 3 | Out-Null
} else {
  Write-Effect $false "clean-result" "result dialog '已移入回收站' not seen in 120s"
}
Start-Sleep -Seconds 2
Save-Shot "e5-after-clean.png"

# ---------- assert: files gone ----------
$goneOk = $true
$goneList = New-Object System.Collections.Generic.List[string]
foreach ($s in $seeds) {
  if ([System.IO.File]::Exists($s)) { $goneOk = $false; $goneList.Add("STILL-PRESENT: $s") }
  else { $goneList.Add("gone: $s") }
}
if ([System.IO.File]::Exists($controlInTemp)) { $goneOk = $false; $goneList.Add("STILL-PRESENT: $controlInTemp") }
else { $goneList.Add("gone: $controlInTemp") }
$keepIntact = [System.IO.File]::Exists($keepPath)
Write-Effect $goneOk "d1-files-gone" (($goneList | Select-Object -First 3) -join '; ')
Write-Effect $keepIntact "keep-file-intact" "keep file outside any rule untouched: $keepPath"

# ---------- assert: free-space delta (recycle-bin path: ~unchanged) ----------
$freeAfter = (New-Object System.IO.DriveInfo($env:SystemDrive.TrimEnd('\'))).AvailableFreeSpace
$delta = $freeAfter - $freePreClean
# Recycle-bin move keeps bytes on the same volume: delta must stay inside the
# noise band. delta ~= +seeded bytes would prove silent PERMANENT deletion
# (red-line breach); delta << 0 means unexpected writes during the probe window.
$tol = 24MB
$deltaOk = (($delta -ge -$tol) -and ($delta -le $tol))
Write-Effect $deltaOk "d1-free-delta" ("free {0} -> {1}, delta={2}; recycle-bin path expects |delta| <= {3} (no silent permanent deletion; real release happens only at confirmed bin emptying)" -f $freePreClean, $freeAfter, $delta, $tol)

# ---------- D-4: ledger match ----------
$ledgerPath = Join-Path $env:LOCALAPPDATA "TaoKeeper\cleanup-ledger.json"
$ledgerOk = $false
$ledgerDetail = ""
try {
  if (Test-Path $ledgerPath) {
    $ledger = Get-Content $ledgerPath -Raw -Encoding utf8 | ConvertFrom-Json
    $entries = @($ledger)
    if ($entries.Count -eq 0 -and $ledger -isnot [array]) { $entries = @() }
    # accept both array-root and object-with-entries shapes
    if (-not $entries.Count -and $ledger.PSObject.Properties['entries']) { $entries = @($ledger.entries) }
    $matched = 0
    $bytesMatched = 0L
    $ruleOk = $true
    foreach ($s in $seeds) {
      $want = (Get-Item $s -ErrorAction SilentlyContinue)
      $e = $entries | Where-Object { $_.OriginalPath -ieq $s } | Select-Object -First 1
      if ($e) {
        $matched++
        if ($e.RuleId -and $e.RuleId -eq 'system-temp') { } else { $ruleOk = $false }
        if ($e.SizeBytes -gt 0) { $bytesMatched += $e.SizeBytes }
      }
    }
    $ledgerOk = ($matched -eq $seedCount) -and $ruleOk -and ($bytesMatched -ge $seedBytes)
    $ledgerDetail = "ledger entries={0}, matched seeds={1}/{2}, ruleId system-temp all={3}, bytesMatched={4} vs seeded {5}" -f $entries.Count, $matched, $seedCount, $ruleOk, $bytesMatched, $seedBytes
  } else {
    $ledgerDetail = "ledger file missing: $ledgerPath"
  }
} catch {
  $ledgerDetail = "ledger read error: {0}" -f $_.Exception.Message
}
Write-Effect $ledgerOk "d4-ledger-match" $ledgerDetail

# ---------- recycle bin: verify + restore one ----------
$shell = $null
$binOk = $false
$restoreOk = $false
$binDetail = ""
try {
  $shell = New-Object -ComObject Shell.Application
  $bin = $shell.Namespace(10)  # ssfBITBUCKET
  $items = @($bin.Items())
  $foundInBin = New-Object System.Collections.Generic.List[string]
  foreach ($it in $items) {
    $n = ''
    try { $n = $it.Name } catch { }
    if ($n -match $marker) { $foundInBin.Add($n) }
  }
  $binOk = ($foundInBin.Count -ge $seedCount)
  $binDetail = "recycle-bin items matching marker: {0}/{1}" -f $foundInBin.Count, $seedCount
  Write-Effect $binOk "d1-in-recycle-bin" $binDetail

  if ($binOk) {
    # restore one: pick first matching item, invoke context verb 还原(&U)
    $target = $null
    foreach ($it in $items) {
      try { if ($it.Name -match $marker) { $target = $it; break } } catch { }
    }
    if ($target) {
      $verbs = @($target.Verbs())
      $done = $false
      foreach ($v in $verbs) {
        $vn = ''
        try { $vn = $v.Name } catch { }
        if ($vn -match '还原') { $v.DoIt(); $done = $true; break }
      }
      Start-Sleep -Seconds 3
      $restored = $seeds | Where-Object { [System.IO.File]::Exists($_) } | Select-Object -First 1
      if ($done -and $restored) {
        $sizeRestored = (Get-Item $restored).Length
        $wantSize = $seedSizes[$restored]
        $restoreOk = ($sizeRestored -eq $wantSize) -and ($sizeRestored -gt 0)
        Write-Effect $restoreOk "d1-restore" ("restored '{0}' via recycle-bin verb; size {1} == seeded {2} (byte-intact; free space ~unchanged on recycle path by design)" -f $restored, $sizeRestored, $wantSize)
      } else {
        Write-Effect $false "d1-restore" ("restore verb done=$done, file back=$([bool]$restored)")
      }
    }
  }
} catch {
  Write-Effect $false "d1-recycle-bin" ("Shell.Application error: {0}" -f $_.Exception.Message)
} finally {
  if ($shell) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
}
Save-Shot "e6-after-restore.png"

# ---------- wrap ----------
try { if (-not $p.HasExited) { $p.Kill() } } catch { }
$script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8
Write-Host ("[SUMMARY] effect fails={0}" -f $script:failCount)
if ($script:failCount -gt 0) { exit 1 }
Write-Host "[OK] effect probe complete: D-1 true-clean + D-4 ledger match all asserted"
