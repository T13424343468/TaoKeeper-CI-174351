# TaoKeeper effect probe D-2: memory page-file plan — real-effect gate.
#
# Acceptance sheet D-2 (产品验收单.md 第五节): 内存优化真生效 — 读优化前占用读数 ->
# 经 MemPage 的 PageFileApplyExecutor 真执行方案 -> 读数可复现变化且与系统实际值一致 ->
# 回滚复核；「只读监控+建议、不主动杀进程」约束全程不破。
#
# Product reality (verified in src, 2026-11-13): MemPage.PageFileApplyExecutor is
# declared (MemPage.xaml.cs:22) and read (:157) but NEVER assigned anywhere —
# OnPlanClick returns before any confirm dialog when the executor is null, so the
# UI cannot execute a plan. This probe therefore:
#   1. captures system page-file ground truth (registry PagingFiles + CIM
#      AutomaticManagedPagefile) before touching the product;
#   2. drives the real UI: --page mem direct launch -> click 性能优先 -> honest
#      plan note assertion;
#   3. records the missing executor wiring as an explicit effect gap and FAILs the
#      real-effect assertion (no fake PASS — 做不出预期结果=不通过);
#   4. independently asserts the red line: a plan click without a confirmed apply
#      must NOT change system settings silently;
#   5. if a confirm dialog DOES appear (future wiring), drives the real confirm
#      flow, asserts the system value changed reproducibly, then restores the
#      captured pre-state and verifies the restore.
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
function Write-Gap([string]$Tag, [string]$Detail) {
  $line = "[GAP] {0}: {1}" -f $Tag, $Detail
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
    $sig = 'using System; using System.Runtime.InteropServices; public static class W32D2 { [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid); }'
    Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue
    $w = [uint32]0
    [W32D2]::GetWindowThreadProcessId($handle, [ref]$w) | Out-Null
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

# ---------- ground truth: system page-file state ----------
$mmKey = 'HKLM:\SYSTEM\CurrentControlSet\Session Manager\Memory Management'
function Get-PagingFilesRaw {
  try { return @((Get-ItemProperty $mmKey -ErrorAction Stop).PagingFiles) } catch { return @('<unreadable>') }
}
function Get-AutoManaged {
  try { return [bool]((Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).AutomaticManagedPagefile) } catch { return '<unreadable>' }
}
function Get-PageFileState {
  return "PagingFiles=[{0}] AutomaticManagedPagefile=[{1}]" -f ((Get-PagingFilesRaw) -join ' | '), (Get-AutoManaged)
}
$pagingBefore = Get-PagingFilesRaw
$autoBefore = Get-AutoManaged
$stateBefore = Get-PageFileState
Write-Host ("[D2] before: {0}" -f $stateBefore)

# ---------- drive the real UI ----------
if (-not (Launch-App 'mem')) { Write-Effect $false "launch" "main window not found in 60s"; Save-Shot "99-no-window.png"; Save-EvidenceAndExit 1 }
if ($null -ne $script:p -and $script:p.HasExited) { Write-Effect $false "app-alive" "app exited during launch"; Save-Shot "99-exited.png"; Save-EvidenceAndExit 1 }

$landed = Wait-ContentText '看得见的内存' 45
Write-Effect ($null -ne $landed) "nav-mem" ("direct-launch --page mem; mem-page marker '{0}'" -f ($(if ($landed) { '看得见的内存 (content verified)' } else { 'NOT seen in 45s' })))
if (-not $landed) { Save-Shot "99-nav-fail.png"; Save-EvidenceAndExit 1 }
Start-Sleep -Seconds 2
Save-Shot "e1-mem-page.png"

$planClicked = Invoke-ContentMatch '性能优先|PlanPerf' 2
Write-Effect $planClicked "d2-plan-clicked" "'性能优先' plan button clicked (plan selection is display-only until the confirmed apply)"
if (-not $planClicked) { Save-Shot "99-plan-fail.png"; Save-EvidenceAndExit 1 }

$note = Wait-ContentText '性能优先：' 10
Write-Effect ($null -ne $note) "d2-plan-note" ("plan note displayed honestly (三步确认/自动备份/一键回滚 wording): '{0}'" -f ($(if ($note) { $note } else { 'NOT seen in 10s' })))
Save-Shot "e2-plan-note.png"

# D-2 real-apply gate: acceptance requires the plan to actually execute with a
# reproducible system reading. Confirm dialog '应用页面文件方案' is the product's
# first confirm step (MemPage.ConfirmAndApplyAsync) — its absence means the
# executor is unwired and no system action is possible from the UI.
$confirmSeen = Wait-ContentText '应用页面文件方案' 8
if ($confirmSeen) {
  Write-Host "[D2] confirm dialog seen (executor wired) — driving the real confirm flow"
  $goClicked = Invoke-ContentMatch '继续' 3
  Write-Effect $goClicked "d2-confirm-step1" "'继续' clicked (step 1 of the user confirm flow, no bypass)"
  Start-Sleep -Seconds 5
  Save-Shot "e3-apply-result.png"
  Invoke-ContentMatch '好' 3 | Out-Null
  Start-Sleep -Seconds 2
  $stateAfterApply = Get-PageFileState
  Write-Host ("[D2] after-apply: {0}" -f $stateAfterApply)
  Write-Effect ($stateAfterApply -ne $stateBefore) "d2-apply-changed" ("system page-file setting changed by the confirmed product flow: before=[{0}] after=[{1}]" -f $stateBefore, $stateAfterApply)
  # rollback: no UI rollback entry is known — restore the captured pre-state and verify
  $rbOk = $false
  try {
    Set-ItemProperty $mmKey -Name PagingFiles -Value ([string[]]$pagingBefore) -ErrorAction Stop
    if ($autoBefore -is [bool]) {
      Set-CimInstance -Query "SELECT * FROM Win32_ComputerSystem" -Property @{ AutomaticManagedPagefile = $autoBefore } -ErrorAction Stop
    }
    Start-Sleep -Seconds 2
    $stateRestored = Get-PageFileState
    $rbOk = ($stateRestored -eq $stateBefore)
    Write-Effect $rbOk "d2-rollback-restored" ("page-file state restored to pre-apply values: [{0}]" -f $stateRestored)
  } catch {
    Write-Effect $false "d2-rollback-restored" ("restore error: {0}" -f $_.Exception.Message)
  }
} else {
  Write-Gap "d2-apply-executor" "MemPage.PageFileApplyExecutor is never assigned in src (declared MemPage.xaml.cs:22, read :157, zero wiring sites) — plan click returns before any dialog; product-side wiring required before D-2 真生效 can be demonstrated"
  Write-Effect $false "d2-real-effect" ("acceptance D-2 真生效 cannot be demonstrated via the real UI: confirm dialog '应用页面文件方案' not seen in 8s after plan click (executor unwired); no fake PASS — recorded as effect gap, product fix needed")
  # red line (independent): a plan click WITHOUT a confirmed apply must never change system settings
  Start-Sleep -Seconds 2
  $stateFinal = Get-PageFileState
  Write-Effect ($stateFinal -eq $stateBefore) "d2-no-silent-change" ("red line held: page-file state unchanged after unconfirmed plan click: [{0}]" -f $stateFinal)
}
Save-Shot "e4-final-state.png"

# ---------- wrap ----------
Stop-AppInstances
$script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8
Write-Host ("[SUMMARY] effect fails={0}" -f $script:failCount)
if ($script:failCount -gt 0) { exit 1 }
Write-Host "[OK] D-2 probe complete (honest display + red line asserted; real-apply gated on executor wiring)"
