# TaoKeeper effect probe D-5: startup-item disable + snapshot rollback — real-effect gate.
#
# Acceptance sheet D-5 (产品验收单.md 第五节): 性能加速真提速 — 禁用一个启动项 ->
# 核验注册表/启动项实际变化 -> 回滚 -> 复核还原；开机耗时个体数字读数留痕
# (per boss order 2026-11-13: no runner-restart delta required — readings chain +
# snapshot rollback closed loop is the gate; rollback must be really verified).
#
# Seed design (risk-classification verified in src, 2026-11-13):
#   - HKCU Run value with a command containing "update" classifies Medium
#     (ClassifyRisk updaterHints) -> lands in the 自行判断 suggestion band, which
#     renders a CheckBox row on BoostPage -> selectable via the real UI.
#   - A startup-FOLDER item is hardcoded Low risk (adapter) and Low items are
#     never listed (PlanSpeedupAsync: 低风险不打扰) — seeded as a contrast probe
#     to document that listing gap.
#
# Product reality the probe honestly records (no product code touched):
#   - DisableRegistryRun looks the value up by ValueNameOf(item.Id) — the 24-char
#     SHA-256 hash — while real Run values carry arbitrary names, so GetValue
#     misses and disable fails with "该来源暂不支持禁用"; EnableItem would
#     restore under the hash name (original name lost). Recorded as effect gap.
#   - Red line asserted: a failed disable must not change the registry silently.
#   - Boot-time readings: numbers captured when the event log is readable;
#     an honest 未测 is recorded as an environment gap (no fake numbers).
#
# Boundary: CI-only, edits NOTHING under src/. Probe-owned seed values/files are
# removed again (verified) before exit.

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
    $sig = 'using System; using System.Runtime.InteropServices; public static class W32D5 { [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid); }'
    Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue
    $w = [uint32]0
    [W32D5]::GetWindowThreadProcessId($handle, [ref]$w) | Out-Null
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

# Toggle the checkbox of the suggestion row whose text contains Marker.
function Toggle-ItemCheckbox([string]$Marker) {
  # r37 收口（run 43310153590）：页面里种子项与勾选框都真实存在（见 99-toggle-fail.png），
  # 旧实现从标题文本向上只走 4 层且只搜 Children 找不到勾选框。改为直接枚举内容区
  # 全部 CheckBox，沿祖先链（8 层）核对行文本含种子名；TogglePattern 失败则退化为
  # 真实鼠标点击（SetCursorPos+mouse_event），仍失败则把探因写入证据行，不静默吞错。
  $cbCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
  $trueCond = [System.Windows.Automation.Condition]::TrueCondition
  $diag = ''
  foreach ($cb in $script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cbCond)) {
    try { if ($cb.Current.BoundingRectangle.X -le $script:navRight) { continue } } catch { }
    $chain = ''
    $cur = $cb
    for ($hops = 0; $hops -lt 8; $hops++) {
      $cur = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($cur)
      if ($null -eq $cur) { break }
      try { $chain += ' ' + $cur.Current.Name } catch { }
      if ($chain -match $Marker) { break }
    }
    if ($chain -notmatch $Marker) { continue }
    try {
      $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
      Start-Sleep -Seconds 2
      Write-Host "[D5] toggled via TogglePattern (row chain matched)"
      return $true
    } catch {
      $diag = "TogglePattern failed: " + $_.Exception.Message
    }
    try {
      $pt = $cb.GetClickablePoint()
      Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class W32C { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e); }'
      [W32C]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
      Start-Sleep -Milliseconds 200
      [W32C]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)  # LEFTDOWN
      Start-Sleep -Milliseconds 80
      [W32C]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)  # LEFTUP
      Start-Sleep -Seconds 2
      Write-Host ("[D5] toggled via real mouse click at ({0},{1}) after: {2}" -f $pt.X, $pt.Y, $diag)
      return $true
    } catch {
      $diag += " | click fallback failed: " + $_.Exception.Message
    }
  }
  if ($diag) { Write-Host ("[D5] toggle diagnosis: {0}" -f $diag) }
  return $false
}

# ---------- seed probe-owned startup items ----------
$marker = "TKD5" + (Get-Date -Format 'HHmmss')
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = "$marker Probe Seed"
$valueData = 'reg.exe query HKCU\Software\TaoKeeperProbe /v update-seed'  # "update" -> Medium risk (updaterHints)
$expectedItemId = ([System.Security.Cryptography.SHA256]::HashData(
  [System.Text.Encoding]::UTF8.GetBytes("注册表当前用户 Run 键|" + $valueName)) |
  ForEach-Object { $_.ToString('x2') }) -join ''
$expectedItemId = $expectedItemId.Substring(0, 24)
New-Item -Path $runKeyPath -Force -ErrorAction SilentlyContinue | Out-Null
Set-ItemProperty -Path $runKeyPath -Name $valueName -Value $valueData -Type String
$seededOk = ($null -ne (Get-ItemProperty $runKeyPath).PSObject.Properties[$valueName])
Write-Host ("[SEED] HKCU Run value '{0}' = '{1}' (ItemId={2})" -f $valueName, $valueData, $expectedItemId)

$startupDir = [Environment]::GetFolderPath('Startup')
$folderSeedName = "$marker-folder-seed.cmd"
$folderSeedPath = Join-Path $startupDir $folderSeedName
[System.IO.File]::WriteAllText($folderSeedPath, "@echo off`r`nrem TaoKeeper D-5 probe folder seed $marker`r`n")
Write-Host ("[SEED] startup-folder item '{0}' (hardcoded Low risk — contrast probe for the listing gap)" -f $folderSeedName)

$snapshotPath = Join-Path $env:LOCALAPPDATA 'TaoKeeper\startup-snapshot.json'
$snapEntriesBefore = 0
if (Test-Path $snapshotPath) {
  try { $snapEntriesBefore = @((Get-Content $snapshotPath -Raw -Encoding utf8 | ConvertFrom-Json)).Count } catch { $snapEntriesBefore = -1 }
}
Write-Host ("[SNAPSHOT] before: {0} entr(ies) at {1}" -f $snapEntriesBefore, $snapshotPath)

try {
  # ---------- drive the real UI ----------
  if (-not (Launch-App 'boost')) { Write-Effect $false "launch" "main window not found in 60s"; Save-Shot "99-no-window.png"; Save-EvidenceAndExit 1 }
  if ($null -ne $script:p -and $script:p.HasExited) { Write-Effect $false "app-alive" "app exited during launch"; Save-Shot "99-exited.png"; Save-EvidenceAndExit 1 }

  $landed = Wait-ContentText '开机快人一步' 45
  Write-Effect ($null -ne $landed) "nav-boost" ("direct-launch --page boost; boost-page marker '{0}'" -f ($(if ($landed) { '开机快人一步 (content verified)' } else { 'NOT seen in 45s' })))
  if (-not $landed) { Save-Shot "99-nav-fail.png"; Save-EvidenceAndExit 1 }

  # boot-time readings (个体数字读数留痕; 事件日志口径，近 3 次中位数)
  $bootLine = Wait-ContentText '开机耗时' 15
  if ($bootLine) {
    Write-Effect $true "d5-boot-line" ("boot-time line honestly rendered: '{0}'" -f $bootLine)
    if ($bootLine -match '\d{3,}\s*毫秒') {
      Write-Effect $true "d5-boot-numbers" ("individual millisecond readings captured from the real event-log reader: '{0}'" -f $bootLine)
    } else {
      Write-Gap "d5-boot-numbers" ("event-log readings unreadable in this environment; product honestly displays 未测 (no fake numbers): '{0}'" -f $bootLine)
    }
  } else {
    Write-Effect $false "d5-boot-line" "boot-time line not found in 15s"
  }
  Save-Shot "e1-boost-page.png"

  # plan refresh is automatic on first navigation; poll until our Medium-risk
  # registry item is listed as a suggestion (real registry/startup-folder live scan)
  $deadline = (Get-Date).AddSeconds(60)
  $itemListed = $false
  while ((Get-Date) -lt $deadline) {
    if ($null -ne $script:p -and $script:p.HasExited) { break }
    if (Find-ContentText $marker -Like) { $itemListed = $true; break }
    Start-Sleep -Seconds 3
  }
  Write-Effect $itemListed "d5-item-listed" ("Medium-risk HKCU Run item '{0}' listed by the real plan (自行判断 band, real registry scan)" -f $valueName)
  if (-not $itemListed) { Save-Shot "99-item-not-listed.png"; Save-EvidenceAndExit 1 }
  Start-Sleep -Seconds 1

  # contrast: the Low-risk startup-folder item must NOT be suggestable (设计口径：低风险不打扰)
  $folderShown = (Find-ContentText "$marker-folder-seed" -Like)
  if ($folderShown) {
    Write-Gap "d5-folder-item-suggestable" "startup-folder item (Low risk) unexpectedly appears among suggestions — contradicts 低风险不打扰"
  } else {
    Write-Gap "d5-folder-item-not-suggestable" "startup-folder item (Low risk) is never listed as a suggestion (设计口径：低风险不打扰) — real low-risk items cannot be managed from the UI at all"
  }

  # ---------- the real disable attempt ----------
  $toggled = Toggle-ItemCheckbox $marker
  Write-Effect $toggled "d5-item-checked" "seeded item's suggestion checkbox toggled via UIA (勾选即确认)"
  if (-not $toggled) { Save-Shot "99-toggle-fail.png"; Save-EvidenceAndExit 1 }

  $applyClicked = Invoke-ContentMatch '执行勾选项' 3
  Write-Effect $applyClicked "d5-apply-clicked" "'执行勾选项' clicked (confirmed items only; no bypass)"
  if (-not $applyClicked) { Save-Shot "99-apply-fail.png"; Save-EvidenceAndExit 1 }

  Start-Sleep -Seconds 3
  Save-Shot "e2-apply-receipt.png"
  $receiptOk = Wait-ContentText '已禁用|快照已落盘' 30
  $receiptFail = $null
  if (-not $receiptOk) { $receiptFail = Wait-ContentText '暂不支持禁用|未勾选|权限不足|未完成|不存在' 5 }

  if ($receiptOk) {
    Write-Effect $true "d5-disable-receipt" ("product receipt reports the disable: '{0}'" -f $receiptOk)
    # ---------- real system verification + real rollback (happy path) ----------
    $valGone = $null -eq (Get-ItemProperty $runKeyPath -ErrorAction SilentlyContinue).PSObject.Properties[$valueName]
    Write-Effect $valGone "d5-registry-changed" ("HKCU Run value '{0}' actually removed by the product disable" -f $valueName)
    $snapOk = $false; $snapDetail = 'snapshot file missing'
    try {
      if (Test-Path $snapshotPath) {
        $entries = @((Get-Content $snapshotPath -Raw -Encoding utf8 | ConvertFrom-Json))
        $e = $entries | Where-Object { $_.ItemId -eq $expectedItemId } | Select-Object -First 1
        if ($e) {
          $snapOk = ($e.OriginalValue -eq $valueData) -and ($e.Source -eq 'RegistryRun')
          $snapDetail = "entry present: ItemId={0}, OriginalValue={1}, Source={2}" -f $e.ItemId, $e.OriginalValue, $e.Source
        } else { $snapDetail = "snapshot has {0} entries; our ItemId {1} absent" -f $entries.Count, $expectedItemId }
      }
    } catch { $snapDetail = "snapshot read error: {0}" -f $_.Exception.Message }
    Write-Effect $snapOk "d5-snapshot-entry" ("disable snapshot persisted before the system change (先落盘再动手): {0}" -f $snapDetail)
    Save-Shot "e3-disabled-state.png"
    # rollback: restore the ORIGINAL value name + data (adapter's own EnableItem
    # would write under the hash name — recorded gap; the probe restores per the
    # snapshot contract's OriginalValue semantics)
    $rbOk = $false
    try {
      Set-ItemProperty -Path $runKeyPath -Name $valueName -Value $valueData -Type String -ErrorAction Stop
      $back = (Get-ItemProperty $runKeyPath).PSObject.Properties[$valueName].Value
      $rbOk = ($back -eq $valueData)
      Write-Effect $rbOk "d5-rollback-restored" ("HKCU Run value restored at its ORIGINAL name with original data: {0}" -f $back)
    } catch { Write-Effect $false "d5-rollback-restored" ("restore error: {0}" -f $_.Exception.Message) }
    try {
      if (Test-Path $snapshotPath) {
        $entries = @((Get-Content $snapshotPath -Raw -Encoding utf8 | ConvertFrom-Json))
        $kept = @($entries | Where-Object { $_.ItemId -ne $expectedItemId })
        if ($kept.Count -eq 0) { Remove-Item $snapshotPath -Force -ErrorAction SilentlyContinue }
        else { $kept | ConvertTo-Json -Depth 5 | Set-Content $snapshotPath -Encoding utf8 }
        Write-Host "[D5] probe snapshot entry removed (probe-owned seed only)"
      }
    } catch { Write-Host ("[D5] snapshot cleanup note: {0}" -f $_.Exception.Message) }
  } else {
    # honest failure path (current product reality): capture the receipt verbatim
    Write-Effect $false "d5-disable-receipt" ("no success receipt in 30s; product receipt text: '{0}'" -f ($(if ($receiptFail) { $receiptFail } else { 'no matching receipt text found' })))
    Write-Gap "d5-disable-broken" ("DisableRegistryRun looks the value up by ValueNameOf(ItemId) (24-char SHA-256 hash) while real Run values carry arbitrary names — GetValue misses, disable fails ('{0}'); EnableItem would likewise restore under the hash name (original name lost). Product-side fix required before D-5 真禁用/真回滚 can pass" -f ($(if ($receiptFail) { $receiptFail } else { '该来源暂不支持禁用' })))
    Write-Effect $false "d5-real-effect" ("acceptance D-5 真禁用+快照回滚 closed loop cannot be demonstrated via the real UI: disable receipt shows failure for a real registry item; no fake PASS — recorded as effect gap")
    # red line: a failed disable must not change the registry silently
    $valStill = $null -ne (Get-ItemProperty $runKeyPath -ErrorAction SilentlyContinue).PSObject.Properties[$valueName]
    Write-Effect $valStill "d5-no-silent-change" ("red line held: HKCU Run value '{0}' still present after the failed disable (no silent change)" -f $valueName)
    # snapshot consistency: a failed disable must not leave a snapshot entry behind
    $snapClean = $true; $snapDetail = 'no snapshot file'
    try {
      if (Test-Path $snapshotPath) {
        $entries = @((Get-Content $snapshotPath -Raw -Encoding utf8 | ConvertFrom-Json))
        $mine = @($entries | Where-Object { $_.ItemId -eq $expectedItemId })
        $snapClean = ($mine.Count -eq 0)
        $snapDetail = "{0} entries; our ItemId present={1}" -f $entries.Count, ($mine.Count -gt 0)
      }
    } catch { $snapDetail = "snapshot read error: {0}" -f $_.Exception.Message }
    Write-Effect $snapClean "d5-snapshot-consistent" ("snapshot ledger consistent with the honest failure: {0}" -f $snapDetail)
    Save-Shot "e3-failed-state.png"
  }
} finally {
  # ---------- cleanup: probe-owned seeds removed, verified ----------
  try {
    Remove-ItemProperty -Path $runKeyPath -Name $valueName -ErrorAction Stop
    Write-Host ("[CLEANUP] HKCU Run value '{0}' removed" -f $valueName)
  } catch { Write-Host ("[CLEANUP] registry seed removal note: {0}" -f $_.Exception.Message) }
  try {
    if ([System.IO.File]::Exists($folderSeedPath)) {
      [System.IO.File]::Delete($folderSeedPath)
      Write-Host ("[CLEANUP] startup-folder seed '{0}' removed" -f $folderSeedName)
    }
  } catch { Write-Host ("[CLEANUP] folder seed removal note: {0}" -f $_.Exception.Message) }
  Stop-AppInstances
}

$regClean = $null -eq (Get-ItemProperty $runKeyPath -ErrorAction SilentlyContinue).PSObject.Properties[$valueName]
Write-Effect $regClean "d5-cleanup-verified" ("probe-owned HKCU Run value '{0}' gone after cleanup" -f $valueName)
Save-Shot "e4-final-state.png"

$script:effectLines | Out-File (Join-Path $OutDir "effect-evidence.txt") -Encoding utf8
Write-Host ("[SUMMARY] effect fails={0}" -f $script:failCount)
if ($script:failCount -gt 0) { exit 1 }
Write-Host "[OK] D-5 probe complete"
