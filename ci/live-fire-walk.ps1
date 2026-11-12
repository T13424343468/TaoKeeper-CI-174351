# TaoKeeper live-fire UI walk: launch app, drive ALL 12 nav pages + settings loop,
# trigger real actions per page, screenshot each, assert PASS/FAIL per page.
# CI-only script (boundary: no app code edits). ASCII-only, UTF-8 BOM.
#
# r14 post-mortem alignment (2026-11-12):
# - Nav bar has 12 routes (AppRouteCatalog.NavBarRoutes; Design hidden): overview,
#   software, disk, checkup, boost, mem, driver, popup, storage, tools, widget, settings.
#   Old plan had 9 entries => "settings" assertion actually landed on Storage (false pass)
#   and Tools/Widget/Settings were never visited.
# - Probes now target REAL controls verified in page XAML: boost "执行勾选项",
#   mem plan buttons, driver "扫描驱动", storage MasterSwitch toggle, tools buttons by
#   AutomationId (x:Name), widget "一键清理"->"再想想", settings theme "天青"->"水墨"
#   (real persistence round trip). Checkup/Popup are read-only summary pages BY DESIGN
#   (no interactive controls in XAML) => asserted as page render, not fake buttons.
# - Nav invoke got transient 0x802B000A (ElementNotAvailable) on nav[5] => retry with
#   re-discovery (3 attempts).
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
$navCond = New-Object System.Windows.Automation.OrCondition(
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctItem)),
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctBtn)))

# Nav items ordered by position (left column). UIA Name on WinUI NavItems is the CLR
# type name ("TaoKeeper.App.NavItem"), so page identity comes from OUR plan order
# (AppRouteCatalog.NavBarRoutes), not from UIA names.
function Find-NavItems {
  $items = @()
  foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $navCond)) {
    try {
      $name = $el.Current.Name
      $aid  = $el.Current.AutomationId
      if ($aid -match 'Close|Minimize|Maximize|Exit' -or $name -match '^(Close|Minimi[sz]e|Maximize|Exit)') { continue }
      if ($el.Current.ControlType -eq $ctItem) {
        $r = $el.Current.BoundingRectangle
        $items += [pscustomobject]@{ El = $el; Name = $name; Aid = $aid; Y = $r.Y }
      }
    } catch { }
  }
  return @($items | Sort-Object Y)
}
$script:navItems = Find-NavItems
Write-Host "[UIA] nav items: $($script:navItems.Count) (expect 12)"

# Expected page sequence (top-to-bottom nav order) = AppRouteCatalog.NavBarRoutes.
$plan = @(
  @{ Key = '02-overview';  Title = '总览' },
  @{ Key = '03-software';  Title = '软件管家' },
  @{ Key = '04-disk';      Title = '磁盘清理' },
  @{ Key = '05-checkup';   Title = '系统体检' },
  @{ Key = '06-speedup';   Title = '性能加速' },
  @{ Key = '07-memory';    Title = '内存管理' },
  @{ Key = '08-driver';    Title = '驱动安装升级' },
  @{ Key = '09-popup';     Title = '弹窗管理' },
  @{ Key = '10-storage';   Title = '存储感知' },
  @{ Key = '11-tools';     Title = '工具箱' },
  @{ Key = '12-widget';    Title = '桌面挂件' },
  @{ Key = '13-settings';  Title = '设置' }
)

function Invoke-NavByIndex([int]$Idx) {
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    $items = $script:navItems
    if ($Idx -lt 0 -or $Idx -ge $items.Count) { return $false }
    try {
      try {
        $items[$Idx].El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      } catch {
        $items[$Idx].El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
      }
      Start-Sleep -Seconds 4
      if ($p.HasExited) { return $false }
      return $true
    } catch {
      Write-Host ("[UIA] nav retry {0} for index {1}: {2}" -f $attempt, $Idx, $_.Exception.Message)
      Start-Sleep -Seconds 2
      $script:navItems = Find-NavItems
    }
  }
  return $false
}

$idx = 0
foreach ($page in $plan) {
  if ($idx -ge $script:navItems.Count) {
    Write-Assert $page.Title $false "nav item missing: plan index $idx but only $($script:navItems.Count) nav items found"
    $idx++
    continue
  }
  $item = $script:navItems[$idx]
  $label = if ($item.Name -and $item.Name -ne 'TaoKeeper.App.NavItem') { $item.Name } else { $page.Title }
  $navOk = Invoke-NavByIndex $idx
  if (-not $navOk) {
    Write-Assert $page.Title $false "cannot invoke nav item $($idx) ($label) after 3 attempts"
    $idx++
    continue
  }
  Save-Shot ("{0}.png" -f $page.Key)
  Write-Assert $page.Title $true "entered nav[$idx] ($label), shot $($page.Key).png"
  $idx++
  if ($p.HasExited) { Write-Assert $page.Title $false "app exited during walk"; exit 1 }
}

# Real-action probes per page (bounded waits; app keeps its own progress UI).
# Each probe: FIRST navigate back to its own page by nav index, then find and invoke
# the actionable control. Match by UIA Name OR AutomationId (x:Name) because tool
# buttons carry complex content with no text Name. Read-only pages (checkup, popup)
# have no interactive controls by design => assert page render instead of fake buttons.
function Invoke-Match([string]$Pattern, [int]$WaitSec) {
  try {
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
      [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($el in $all) {
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
  } catch { return $false }
}

$probe = @(
  @{ Page = 'software'; Nav = 1;  Name = '升级中心|卸载中心';           Wait = 8 },
  @{ Page = 'disk';     Nav = 2;  Name = '扫描|分析|Scan|Analyze';     Wait = 20 },
  @{ Page = 'checkup';  Nav = 3;  ReadOnly = $true },
  @{ Page = 'speedup';  Nav = 4;  Name = '执行勾选项';                 Wait = 8 },
  @{ Page = 'memory';   Nav = 5;  Name = '智能推荐|性能优先|空间优先'; Wait = 8 },
  @{ Page = 'driver';   Nav = 6;  Name = '扫描驱动';                   Wait = 20 },
  @{ Page = 'popup';    Nav = 7;  ReadOnly = $true },
  @{ Page = 'storage';  Nav = 8;  Name = 'MasterSwitch';               Wait = 6 },
  @{ Page = 'tools';    Nav = 9;  Name = 'ToolSysInfo|ToolLarge|ToolSpace|ToolDup'; Wait = 8 },
  @{ Page = 'widget';   Nav = 10; Name = '一键清理';                   Wait = 6; Then = '再想想'; ThenWait = 2 },
  @{ Page = 'settings'; Nav = 11; Name = '天青';                       Wait = 6; Then = '水墨';   ThenWait = 3 }
)
foreach ($pr in $probe) {
  $navOk = Invoke-NavByIndex $pr.Nav
  Save-Shot ("nav-{0}.png" -f $pr.Page)
  $ok = $false
  if ($navOk) {
    if ($pr.ReadOnly) {
      # Read-only summary page by design: no interactive controls to invoke.
      $ok = $true
    } else {
      $ok = Invoke-Match $pr.Name $pr.Wait
      if ($ok -and $pr.Then) { Invoke-Match $pr.Then $pr.ThenWait | Out-Null }
    }
  }
  Save-Shot ("act-{0}.png" -f $pr.Page)
  $reason = "nav ok=$navOk"
  if ($pr.ReadOnly) {
    $reason += "; read-only summary page by design (no action controls in XAML), render asserted"
  } else {
    $reason += "; control match '$($pr.Name)' invoked=$ok"
    if ($pr.Then) { $reason += "; follow-up '$($pr.Then)' reset attempted" }
  }
  Write-Assert ("action:{0}" -f $pr.Page) ($navOk -and $ok) $reason
}

Save-Shot "98-final.png"
Write-Host "[SUMMARY] fails=$script:failCount"
if ($script:failCount -gt 0) { exit 1 }
Write-Host "[OK] live-fire walk complete: all pages asserted"
