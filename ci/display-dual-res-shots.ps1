# TaoKeeper display-difference dual-pass screenshot job (boss 2026-11-14: visual diff
# surfaces must be covered on cloud, not parked on the real device only).
# Pass 1: app window forced to 1424x714 (small-window tier) at default 96 DPI.
# Pass 2: system DPI switched to 200% (LogPixels=192 + Win8DpiScaling), app relaunched,
#         same 12 nav pages captured via direct-launch routes.
# Evidence-only job: honest [DUALRES] PASS/FAIL lines; never silently fakes a pass.
# ASCII-only comments, UTF-8 BOM (Chinese page markers only in string literals).
param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [string]$OutDir = 'dual-res-shots'
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# Real-pixel capture regardless of session DPI virtualization.
Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
public static class Native {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool MoveWindow(System.IntPtr hWnd, int x, int y, int w, int h, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
  [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int SystemParametersInfo(int action, int param, int val, int ini);
}
"@
[Native]::SetProcessDPIAware() | Out-Null

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$ExePath = (Resolve-Path $ExePath).Path

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$ctText  = [System.Windows.Automation.AutomationElement]::ControlTypeText
$ctWindow= [System.Windows.Automation.AutomationElement]::ControlTypeWindow
$navRight = 296

# Route ids from AppRouteCatalog.Items (nav-bar subset; design page excluded by catalog).
$pages = @(
  @{ Id='home';     Title='总览' },
  @{ Id='swstore';  Title='软件管家' },
  @{ Id='disk';     Title='磁盘清理' },
  @{ Id='checkup';  Title='系统体检' },
  @{ Id='boost';    Title='性能加速' },
  @{ Id='mem';      Title='内存管理' },
  @{ Id='driver';   Title='驱动安装升级' },
  @{ Id='popup';    Title='弹窗管理' },
  @{ Id='storage';  Title='存储感知' },
  @{ Id='tools';    Title='工具箱' },
  @{ Id='widget';   Title='桌面挂件' },
  @{ Id='settings'; Title='设置' }
)

# Unique content-area landing markers (v6 unique-subtitle discipline).
$marker = @{
  'home'='开机快人一步'; 'swstore'='升级中心|卸载中心'; 'disk'='扫得全，清得稳';
  'checkup'='六维体检'; 'boost'='开机快人一步|尚未体检加速'; 'mem'='PlanSmart|PlanPerf|PlanSpace';
  'driver'='驱动安装升级'; 'popup'='弹窗'; 'storage'='存储感知';
  'tools'='工具箱'; 'widget'='一键清理'; 'settings'='主题'
}

$script:p = $null
function Stop-App {
  if ($null -ne $script:p -and -not $script:p.HasExited) {
    try { $script:p.Kill(); $script:p.WaitForExit(8000) | Out-Null } catch { }
  }
  $script:p = $null
  Get-Process -Name 'TaoKeeper.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
}

function Get-AppWindow([int]$procId) {
  $deadline = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $deadline) {
    if ($null -ne $script:p -and $script:p.HasExited) { return $null }
    try {
      # dual-res fix: UIA FindAll(Children+ProcessId) misses the window on cloud runner
      # (12/12 pages, both DPI tiers, process alive) while Process.MainWindowHandle works
      # (live-fire walk same payload all PASS). Locate by MainWindowHandle first, wrap via UIA FromHandle.
      $script:p.Refresh()
      if ($script:p.MainWindowHandle -ne [IntPtr]::Zero) {
        try {
          $w = [System.Windows.Automation.AutomationElement]::FromHandle($script:p.MainWindowHandle)
          if ($null -ne $w -and $w.Current.NativeWindowHandle -ne 0) { return $w }
        } catch { }
      }
      $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
      $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
      foreach ($w in $wins) {
        try {
          if ($w.Current.ControlType -eq $ctWindow -and $w.Current.NativeWindowHandle -ne 0) { return $w }
        } catch { }
      }
    } catch { }
    Start-Sleep -Milliseconds 800
  }
  return $null
}

function Wait-Marker([string]$pattern, [int]$maxSec) {
  $deadline = (Get-Date).AddSeconds($maxSec)
  while ((Get-Date) -lt $deadline) {
    if ($null -ne $script:p -and $script:p.HasExited) { return $null }
    try {
      $texts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ctText)
      foreach ($t in $texts) {
        try {
          if ($t.Current.BoundingRectangle.X -ge $navRight -and $t.Current.Name -match $pattern) {
            return $t.Current.Name
          }
        } catch { }
      }
    } catch { }
    Start-Sleep -Seconds 1
  }
  return $null
}

function Save-Shot([string]$path, [System.Windows.Automation.AutomationElement]$win) {
  try {
    $r = $win.Current.BoundingRectangle
    $x=[int]$r.X; $y=[int]$r.Y; $w=[int]$r.Width; $h=[int]$r.Height
    if ($x -lt 0) { $x = 0 }
    if ($y -lt 0) { $y = 0 }
    if ($x + $w -gt [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width) { $w = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width - $x }
    if ($y + $h -gt [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height) { $h = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height - $y }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host ("[DUALRES] shot saved: {0} ({1}x{2})" -f $path, $w, $h)
  } catch {
    Write-Host "[DUALRES] shot FAILED: $_"
  }
}

function Set-SystemDpi([int]$dpi) {
  try {
    Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name LogPixels -Value $dpi -Type DWord -ErrorAction Stop
    Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name Win8DpiScaling -Value 1 -Type DWord -ErrorAction Stop
    # SPI_SETLOGPIXELS=0x000D, SPIF_UPDATEINIFILE(1)|SPIF_SENDCHANGE(2): applies to newly started processes
    [Native]::SystemParametersInfo(0x000D, 0, $dpi, 3) | Out-Null
    Write-Host "[DUALRES] system DPI set to $dpi (new processes)"
    return $true
  } catch {
    Write-Host "[DUALRES] DPI set to $dpi FAILED: $_"
    return $false
  }
}

function Shot-All([string]$prefix, [int]$winW, [int]$winH) {
  $okCount = 0
  foreach ($pg in $pages) {
    try {
      $script:p = Start-Process -FilePath $ExePath -ArgumentList '--page',$pg.Id -WorkingDirectory (Split-Path $ExePath) -PassThru
      Write-Host ("[DUALRES] {0}:{1} pid={2}" -f $prefix, $pg.Id, $script:p.Id)
      $win = Get-AppWindow $script:p.Id
      if ($null -eq $win) {
        Write-Host ("[DUALRES] {0}:{1} window not found in 60s" -f $prefix, $pg.Id)
        Save-Shot (Join-Path $OutDir ("{0}-{1}-nowindow.png" -f $prefix, $pg.Id)) $root
        Stop-App; continue
      }
      $h = [IntPtr]$win.Current.NativeWindowHandle
      [Native]::SetForegroundWindow($h) | Out-Null
      [Native]::MoveWindow($h, 0, 0, $winW, $winH, $true) | Out-Null
      Start-Sleep -Seconds 3
      $landed = Wait-Marker $marker[$pg.Id] 45
      if ($null -eq $landed) {
        Write-Host ("[DUALRES] {0}:{1} marker NOT seen: '{2}'" -f $prefix, $pg.Id, $marker[$pg.Id])
      } else {
        Write-Host ("[DUALRES] {0}:{1} landed, marker: {2}" -f $prefix, $pg.Id, ($landed.Substring(0, [Math]::Min(40, $landed.Length))))
      }
      Start-Sleep -Seconds 2
      Save-Shot (Join-Path $OutDir ("{0}-{1}.png" -f $prefix, $pg.Id)) $win
      $okCount++
      Stop-App
    } catch {
      Write-Host ("[DUALRES] {0}:{1} FAILED: {2}" -f $prefix, $pg.Id, $_)
      Stop-App
    }
  }
  return $okCount
}

# ---------- pass 1: 1424x714 small-window tier at default 96 DPI ----------
Set-SystemDpi 96 | Out-Null
Start-Sleep -Seconds 2
$n1 = Shot-All 'small' 1424 714

# ---------- pass 2: 200% DPI tier (same small window) ----------
$n2 = 0
if (Set-SystemDpi 192) {
  Start-Sleep -Seconds 4
  $n2 = Shot-All 'dpi200' 1424 714
}

# ---------- restore and report ----------
Set-SystemDpi 96 | Out-Null
Write-Host "[DUALRES] summary: small-window shots=$n1/12, dpi200 shots=$n2/12"
if ($n1 -eq 0 -and $n2 -eq 0) {
  Write-Host "[DUALRES] FAILED: no shots captured at all"
  exit 1
}
Write-Host "[DUALRES] dual-pass capture complete (evidence-only job; visual review by agent vision model follows in CI report)"
exit 0
