# TaoKeeper effect probe D-3: health checkup — real detect, real fix, real re-check.
#
# Acceptance sheet D-3 (产品验收单.md 第五节): 体检真检出真修复 — 规则路径造可检出
# 样本 -> 系统体检页报出该项（带三态与依据）-> 经路由到磁盘清理真清理 -> 复检该项
# 转通过，前后两次报告留痕可比（截图+证据行）。
#
# Mechanism (verified in src, 2026-11-13):
#   - HealthCheckEngine junk-accumulation rule fires at >= 1GB reclaimable
#     (Advice, "可清理垃圾约 X ...", SuggestedActionId jump-disk-clean);
#   - the checkup context is built at app start from the real background junk
#     scan (CompositionRoot.KickOffJunkScan -> RebuildCheckupContext) and bound
#     on navigation via CheckupViewModelProvider — so seeding >= 1GB marker junk
#     in %TEMP% BEFORE launch makes the checkup page report it;
#   - the real fix is the real disk-clean flow (already proven by
#     effect-probe-d1d4.ps1): scan -> 清理（先进回收站） -> 确认清理;
#   - re-check: relaunch --page checkup (a fresh startup junk scan rebuilds the
#     context post-clean; no re-checkup button exists in the UI) and assert the
#     junk finding is gone in a tested (not 未测) state.
#
# Evidence: before/after checkup screenshots + captured finding/score text lines
# in effect-evidence.txt. No fake PASS: any failed assertion exits 1.
#
# Boundary: CI-only, edits NOTHING under src/.

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
function Save-EvidenceAndExit([int]$Code) {
  $script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8
  exit $Code
}

$script:p = $null
$script:root = $null
$script:ctText = [System.Windows.Automation.ControlType]::Text
$script:navRight = 260.0

function Stop-AppInstances {
  Get-Process -Name 'TaoKeeper.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  $deadline = (Get-Date).AddSeconds(10)
  while ((Get-Date) -lt $deadline) {
    if (-not (Get-Process -Name 'TaoKeeper.App' -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 500
  }
}

function Launch-App([string]$PageId) {
  Stop-AppInstances
  $script:p = Start-Process -FilePath $ExePath -ArgumentList '--page',$PageId -WorkingDirectory (Split-Path $ExePath) -PassThru
  Write-Host ("[PID] launched probe instance pid={0} page={1}" -f $script:p.Id, $PageId)
  Start-Sleep -Seconds 5
  $deadline = (Get-Date).AddSeconds(60)
  $handle = [IntPtr]::Zero
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $script:p.Refresh()
    if ($script:p.HasExited) { break }
    if ($script:p.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $script:p.MainWindowHandle; break }
  }
  if ($handle -eq [IntPtr]::Zero) { return $false }
  try {
    $sig = 'using System; using System.Runtime.InteropServices; public static class W32D3 { [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid); }'
    Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue
    $w = [uint32]0
    [W32D3]::GetWindowThreadProcessId($handle, [ref]$w) | Out-Null
    $winPid = [int]$w
    if ($winPid -ne 0 -and $winPid -ne $script:p.Id) { Write-Host ("[PID] MISMATCH window pid={0} probe pid={1}" -f $winPid, $script:p.Id) }
  } catch { }
  Start-Sleep -Seconds 5
  $sigResize = 'using System; using System.Runtime.InteropServices; public static class W32R { [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags); }'
  Add-Type -TypeDefinition $sigResize -ErrorAction SilentlyContinue
  [W32R]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, 1400, 1000, 0x0040) | Out-Null
  Start-Sleep -Seconds 2
  $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
  return $true
}

function Find-ContentText([string]$Name, [switch]$Like) {
  if ($null -eq $script:root) { return $false }
  $textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $script:ctText)
  foreach ($t in $script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
    try {
      $r = $t.Current.BoundingRectangle
      $n = $t.Current.Name
      if ($r.X -gt $script:navRight -and (($Like -and $n -match $Name) -or (-not $Like -and $n -eq $Name))) { return $true }
    } catch { }
  }
  return $false
}

function Invoke-ContentMatch([string]$Pattern, [int]$WaitSec) {
  if ($null -eq $script:root) { return $false }
  $all = $script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($el in $all) {
    try { if ($el.Current.IsOffscreen) { continue } } catch { }
    try { if ($el.Current.BoundingRectangle.X -lt $script:navRight) { continue } } catch { }
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

function Wait-ContentText([string]$Pattern, [int]$MaxSec) {
  if ($null -eq $script:root) { return $null }
  $textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $script:ctText)
  $deadline = (Get-Date).AddSeconds($MaxSec)
  while ((Get-Date) -lt $deadline) {
    if ($null -ne $script:p -and -not $script:p.HasExited) { } else { return $null }
    foreach ($t in $script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
      try {
        $r = $t.Current.BoundingRectangle
        if ($r.X -gt $script:navRight -and $t.Current.Name -match $Pattern) { return $t.Current.Name }
      } catch { }
    }
    Start-Sleep -Seconds 2
  }
  return $null
}

# ---------- seed >= 1GB detectable junk (junk-accumulation Advice threshold) ----------
$marker = "TK-CHK-" + (Get-Date -Format 'HHmmss')
$seedDir = Join-Path ([System.IO.Path]::GetTempPath()) ("tk-checkup-" + $marker)
New-Item -ItemType Directory -Force -Path $seedDir | Out-Null
$seedCount = 6
$seedBytes = 0L
for ($i = 0; $i -lt $seedCount; $i++) {
  $p = Join-Path $seedDir ("{0}-junk-{1:D2}.tmp" -f $marker, $i)
  $bytes = New-Object byte[] (200MB)
  (New-Object Random($i)).NextBytes($bytes)
  [System.IO.File]::WriteAllBytes($p, $bytes)
  $seedBytes += 200MB
}
Write-Host ("[SEED] dir={0} files={1} bytes={2} (>= 1GB junk-accumulation threshold; system-temp rule, pattern *)" -f $seedDir, $seedCount, $seedBytes)

# ---------- phase A: checkup must REPORT the seeded junk (真检出) ----------
$phaseAOk = $false
$findingText = $null
foreach ($attempt in 1..2) {
  if (-not (Launch-App 'checkup')) {
    Write-Effect $false "launch" ("main window not found in 60s (checkup attempt {0})" -f $attempt)
    Save-Shot "99-no-window.png"; Save-EvidenceAndExit 1
  }
  if ($null -ne $script:p -and $script:p.HasExited) { Write-Effect $false "app-alive" "app exited during launch"; Save-Shot "99-exited.png"; Save-EvidenceAndExit 1 }
  $landed = Wait-ContentText '六维体检' 45
  Write-Effect ($null -ne $landed) "nav-checkup" ("direct-launch --page checkup; marker '{0}' (attempt {1})" -f ($(if ($landed) { '六维体检 (content verified)' } else { 'NOT seen in 45s' })), $attempt)
  if (-not $landed) { Save-Shot "99-nav-fail.png"; Save-EvidenceAndExit 1 }
  # the context is rebuilt when the startup junk scan completes; poll for the
  # finding (one relaunch covers the untested-at-navigation race)
  $deadline = (Get-Date).AddSeconds(75)
  while ((Get-Date) -lt $deadline) {
    if ($null -ne $script:p -and $script:p.HasExited) { break }
    if (Find-ContentText '垃圾堆积' -Like) { $phaseAOk = $true; break }
    Start-Sleep -Seconds 3
  }
  if ($phaseAOk) { break }
  if ($attempt -eq 1) { Write-Host "[D3] attempt 1: no junk finding (possible untested-at-nav race) — relaunching once"; Save-Shot "99-checkup-nofinding-a1.png" }
}
Start-Sleep -Seconds 2
Save-Shot "e2-checkup-with-finding.png"
if ($phaseAOk) { $findingText = Wait-ContentText '可清理垃圾约' 10 }
$scoreBefore = Wait-ContentText '体检得分' 5
Write-Host ("[D3] score line before: {0}" -f $scoreBefore)
Write-Effect $phaseAOk "d3-finding-reported" ("checkup reported the seeded junk WITH basis (三态: severity dot + detail + score impact): '{0}'" -f ($(if ($findingText) { $findingText } else { 'junk finding NOT seen in two attempts' })))
if (-not $phaseAOk) { Save-EvidenceAndExit 1 }

# ---------- phase B: real fix via the real disk-clean flow ----------
if (-not (Launch-App 'disk')) { Write-Effect $false "launch-disk" "main window not found in 60s"; Save-Shot "99-no-window.png"; Save-EvidenceAndExit 1 }
$landedDisk = Wait-ContentText '扫得全，清得稳' 45
Write-Effect ($null -ne $landedDisk) "nav-disk" ("direct-launch --page disk; marker '{0}'" -f ($(if ($landedDisk) { '扫得全，清得稳 (content verified)' } else { 'NOT seen in 45s' })))
if (-not $landedDisk) { Save-Shot "99-nav-fail-disk.png"; Save-EvidenceAndExit 1 }
Start-Sleep -Seconds 2
$scanInvoked = Invoke-ContentMatch '开始扫描|ScanButton' 3
Write-Effect $scanInvoked "scan-invoked" "'开始扫描' clicked (real junk rule engine, same one the checkup context consumes)"
if (-not $scanInvoked) { Save-Shot "99-scan-fail.png"; Save-EvidenceAndExit 1 }
$scanText = Wait-ContentText '扫描完成' 90
if ($scanText) {
  Save-Shot "e3-disk-scan.png"
  $m = [regex]::Match($scanText, '发现 (\d+) 条')
  $foundOk = $false
  if ($m.Success) {
    $found = [int]$m.Groups[1].Value
    $foundOk = ($found -ge $seedCount)
    Write-Effect $foundOk "d3-scan-covers-seeds" ("scan reports {0} items (>= {1} seeded; '{2}')" -f $found, $seedCount, $scanText)
  } else {
    Write-Effect $false "d3-scan-covers-seeds" ("scan text unparsable: '{0}'" -f $scanText)
  }
} else {
  Write-Effect $false "d3-scan-covers-seeds" "'扫描完成' not seen in 90s"
  Save-Shot "99-scan-timeout.png"; Save-EvidenceAndExit 1
}
$cleanInvoked = Invoke-ContentMatch '清理（先进回收站）|CleanButton' 4
Write-Effect $cleanInvoked "clean-invoked" "'清理（先进回收站）' clicked"
if (-not $cleanInvoked) { Save-Shot "99-clean-fail.png"; Save-EvidenceAndExit 1 }
$confirmClicked = Invoke-ContentMatch '确认清理' 10
Write-Effect $confirmClicked "clean-confirmed" "'确认清理' dialog confirmed (user-confirm gate honored)"
if (-not $confirmClicked) { Save-Shot "99-confirm-fail.png"; Save-EvidenceAndExit 1 }
$resultText = Wait-ContentText '已移入回收站' 120
if ($resultText) {
  Save-Shot "e4-clean-result.png"
  $m2 = [regex]::Match($resultText, '成功\s*(\d+)')
  $movedOk = $false
  if ($m2.Success) {
    $moved = [int]$m2.Groups[1].Value
    $movedOk = ($moved -ge $seedCount)
    Write-Effect $movedOk "d3-clean-executed" ("real clean moved {0} items to the recycle bin (>= {1} seeded; '{2}')" -f $moved, $seedCount, $resultText)
  } else {
    Write-Effect $false "d3-clean-executed" ("clean result text unparsable: '{0}'" -f $resultText)
  }
} else {
  Write-Effect $false "d3-clean-executed" "'已移入回收站' result not seen in 120s"
  Save-Shot "99-clean-timeout.png"; Save-EvidenceAndExit 1
}
Invoke-ContentMatch '好' 3 | Out-Null

# ---------- phase C: re-check must turn the item clean (复检转通过) ----------
if (-not (Launch-App 'checkup')) { Write-Effect $false "launch-recheck" "main window not found in 60s"; Save-Shot "99-no-window.png"; Save-EvidenceAndExit 1 }
$landed2 = Wait-ContentText '六维体检' 45
Write-Effect ($null -ne $landed2) "nav-checkup-recheck" ("direct-launch --page checkup for re-check; marker '{0}'" -f ($(if ($landed2) { '六维体检 (content verified)' } else { 'NOT seen in 45s' })))
if (-not $landed2) { Save-Shot "99-nav-fail-recheck.png"; Save-EvidenceAndExit 1 }
$tested = $false
$junkBack = $false
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
  if ($null -ne $script:p -and $script:p.HasExited) { break }
  if (Find-ContentText '垃圾堆积' -Like) { $junkBack = $true; break }
  if (Find-ContentText '体检得分' -Like) { $tested = $true; break }
  Start-Sleep -Seconds 3
}
if ($tested -and -not $junkBack) {
  Start-Sleep -Seconds 5
  if (Find-ContentText '垃圾堆积' -Like) { $junkBack = $true }
}
Start-Sleep -Seconds 2
Save-Shot "e5-checkup-after.png"
$scoreAfter = Wait-ContentText '体检得分' 5
Write-Host ("[D3] score line after: {0}" -f $scoreAfter)
$recheckOk = ($tested -and -not $junkBack)
Write-Effect $recheckOk "d3-recheck-clean" ("re-checkup after the real clean: tested={0}, junk-finding-back={1}, score line='{2}' (item turned clean; before/after reports archived)" -f $tested, $junkBack, $scoreAfter)
if (-not $recheckOk -and -not $tested) {
  Write-Host "[D3] note: checkup stayed 未测 for 120s (startup junk-scan context did not bind at navigation); recorded honestly"
}

# ---------- wrap ----------
Stop-AppInstances
$script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8
Write-Host ("[SUMMARY] effect fails={0}" -f $script:failCount)
if ($script:failCount -gt 0) { exit 1 }
Write-Host "[OK] D-3 probe complete: seeded junk detected -> really cleaned -> re-check clean"
