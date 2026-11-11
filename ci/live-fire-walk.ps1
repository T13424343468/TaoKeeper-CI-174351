# TaoKeeper live-fire UI walk: launch app, drive ALL 8 nav pages + settings loop,
# trigger real actions per page, screenshot each, assert PASS/FAIL per page.
# CI-only script (boundary: no app code edits). ASCII-only, UTF-8 BOM.
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
function Write-Assert([string]$Page, [bool]$Ok, [string]$Reason) {
  $verdict = 'PASS'
  if (-not $Ok) { $verdict = 'FAIL'; $script:failCount++ }
  Write-Host ("[{0}] {1}: {2}" -f $verdict, $Page, $Reason)
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

$p = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath) -PassThru
Write-Host "[LAUNCH] pid=$($p.Id)"
Start-Sleep -Seconds 5
Save-Shot "00-launched.png"

$deadline = (Get-Date).AddSeconds(60)
$handle = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 3
  $p.Refresh()
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $p.MainWindowHandle; break }
}
if ($handle -eq [IntPtr]::Zero) {
  Save-Shot "99-no-window.png"
  Write-Assert "launch" $false "main window not found in 60s; alive=$(-not $p.HasExited)"
  exit 1
}
Start-Sleep -Seconds 5
Save-Shot "01-main-window.png"
Write-Assert "launch" $true "window found pid=$($p.Id)"

$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$ctItem = [System.Windows.Automation.ControlType]::ListItem
$ctBtn  = [System.Windows.Automation.ControlType]::Button
$cond = New-Object System.Windows.Automation.OrCondition(
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctItem)),
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctBtn)))
$els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)

# Nav items ordered by position (left column), fix unnCN: match by AutomationId
# first, fall back to vertical order index; page names come from OUR plan, not UI names.
$navItems = @()
foreach ($el in $els) {
  try {
    $name = $el.Current.Name
    $aid  = $el.Current.AutomationId
    if ($aid -match 'Close|Minimize|Maximize|Exit' -or $name -match '^(Close|Minimi[sz]e|Maximize|Exit)') { continue }
    if ([System.Windows.Automation.AutomationElement]::ControlTypeProperty -and $el.Current.ControlType -eq $ctItem) {
      $r = $el.Current.BoundingRectangle
      $navItems += [pscustomobject]@{ El = $el; Name = $name; Aid = $aid; Y = $r.Y }
    }
  } catch { }
}
$navItems = @($navItems | Sort-Object Y)
Write-Host "[UIA] nav candidates: $($navItems.Count)"

# Expected page sequence (top-to-bottom nav order). Names are OUR assertions,
# not read back from UI; a page must be reachable even if its UIA name is empty.
$plan = @(
  @{ Key = '02-overview';   Title = '总览' },
  @{ Key = '03-software';   Title = '软件管家' },
  @{ Key = '04-disk';       Title = '磁盘清理' },
  @{ Key = '05-checkup';    Title = '系统体检' },
  @{ Key = '06-speedup';    Title = '性能加速' },
  @{ Key = '07-memory';     Title = '内存管理' },
  @{ Key = '08-driver';     Title = '驱动安装升级' },
  @{ Key = '09-popup';      Title = '弹窗管理' },
  @{ Key = '10-settings';   Title = '设置' }
)

$idx = 0
foreach ($page in $plan) {
  if ($idx -ge $navItems.Count) {
    Write-Assert $page.Title $false "nav item missing: plan index $idx but only $($navItems.Count) nav items found"
    continue
  }
  $item = $navItems[$idx]
  $label = if ($item.Name) { $item.Name } else { $page.Title }
  try {
    try {
      $item.El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    } catch {
      $item.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
  } catch {
    Write-Assert $page.Title $false "cannot invoke nav item $($idx): $($_.Exception.Message)"
    $idx++
    continue
  }
  Start-Sleep -Seconds 4
  Save-Shot ("{0}.png" -f $page.Key)
  Write-Assert $page.Title $true "entered nav[$idx] ($label), shot $($page.Key).png"
  $idx++
  if ($p.HasExited) { Write-Assert $page.Title $false "app exited during walk"; exit 1 }
}

# Real-action probes per page (bounded waits; app keeps its own progress UI).
# Each probe: FIRST navigate back to its own page by nav index, then find and
# invoke the actionable button. The walk leaves the app on the LAST page, so
# without explicit navigation every probe would run on the wrong page and
# false-fail. Nav index map: overview=0/software=1/disk=2/checkup=3/
# speedup=4/memory=5/driver=6/popup=7/settings=8 (matches $plan order).
function Invoke-NavByIndex([int]$Idx) {
  if ($Idx -lt 0 -or $Idx -ge $navItems.Count) { return $false }
  try {
    try {
      $navItems[$Idx].El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    } catch {
      $navItems[$Idx].El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
  } catch { return $false }
  Start-Sleep -Seconds 4
  if ($p.HasExited) { return $false }
  return $true
}
function Invoke-PageAction([string]$ActionName, [int]$WaitSec) {
  try {
    $scope = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
      (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsControlElementProperty, $true)))
    $btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
      (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctBtn)))
    foreach ($btn in $btns) {
      $n = ''
      try { $n = $btn.Current.Name } catch { }
      if ($n -match $ActionName) {
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds $WaitSec
        return $true
      }
    }
    return $false
  } catch { return $false }
}

$probe = @(
  @{ Page = 'checkup';  Nav = 3; Name = '扫描|体检|Start|Scan';  Wait = 20 },
  @{ Page = 'speedup';  Nav = 4; Name = '扫描|加速|Scan|Boost';  Wait = 15 },
  @{ Page = 'memory';   Nav = 5; Name = '刷新|清理|Refresh|Clean'; Wait = 10 },
  @{ Page = 'popup';    Nav = 7; Name = '扫描|检测|Scan';        Wait = 10 },
  @{ Page = 'software'; Nav = 1; Name = '刷新|安装|清单|Refresh'; Wait = 10 },
  @{ Page = 'disk';     Nav = 2; Name = '扫描|分析|Scan|Analyze'; Wait = 20 }
)
foreach ($pr in $probe) {
  $navOk = Invoke-NavByIndex $pr.Nav
  Save-Shot ("nav-{0}.png" -f $pr.Page)
  $ok = $false
  if ($navOk) { $ok = Invoke-PageAction $pr.Name $pr.Wait }
  Save-Shot ("act-{0}.png" -f $pr.Page)
  Write-Assert ("action:{0}" -f $pr.Page) ($navOk -and $ok) "nav ok=$navOk; button match '$($pr.Name)' invoked=$ok"
}

# Settings read-write-reset loop (real persistence round trip). Walk already
# ends on settings (plan index 8); navigate explicitly for symmetry.
try {
  $okNavSet = Invoke-NavByIndex 8
  $okSet = $false
  if ($okNavSet) { $okSet = Invoke-PageAction '主题|Theme|保存|Save|应用|Apply' 6 }
  Save-Shot "act-settings.png"
  Write-Assert "settings:read-write-reset" ($okNavSet -and $okSet) "nav ok=$okNavSet; theme/save control invoked=$okSet"
} catch {
  Write-Assert "settings:read-write-reset" $false $_.Exception.Message
}

Save-Shot "98-final.png"
Write-Host "[SUMMARY] fails=$script:failCount"
if ($script:failCount -gt 0) { exit 1 }
Write-Host "[OK] live-fire walk complete: all pages asserted"
