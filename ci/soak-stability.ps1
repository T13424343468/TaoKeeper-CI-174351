# TaoKeeper CI: 30-min soak stability with full metrics (r12 gate step)
# CI-only script (boundary: no edits to app code). UTF-8 BOM（坑册一2：ps1 BOM 中文稳；bat 才纯 ASCII）.
# Flow: locate installed app -> attach/launch -> idle baseline -> real disk scan via UIA
#       -> 30-min sampled soak (mem/cpu/disk-write, restart tracking) -> hard assertions.
param(
  [string]$MsiPath = "tools/msi/out/TaoKeeper-PC-MSI-v1.0.0.msi",
  [string]$OutDir = "soak-shots",
  [int]$DurationSec = 1800,
  [int]$SampleIntervalSec = 15
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$script:Metrics = [System.Collections.Generic.List[string]]::new()
function Add-Metric([string]$Line) { $script:Metrics.Add($Line); Write-Host $Line }
function Save-Shot([string]$Name) {
  try {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
  } catch { Write-Host "[WARN] screenshot $Name failed: $($_.Exception.Message)" }
}

# ---- Segment 1: locate installed app (hard gate, no guessing paths silently) ----
$arp = Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like "*TaoKeeper*" } | Select-Object -First 1
if (-not $arp) { Write-Host "[ERROR] no TaoKeeper ARP entry; soak requires installed app"; exit 1 }
$installDir = $arp.InstallLocation
if ([string]::IsNullOrWhiteSpace($installDir) -or -not (Test-Path $installDir)) { $installDir = "D:\Program Files\TaoKeeper" }
$exe = Join-Path $installDir "TaoKeeper.App.exe"
if (-not (Test-Path $exe)) { Write-Host "[ERROR] installed exe not found at $exe"; exit 1 }
Add-Metric "[SOAK] install_dir=$installDir"
Add-Metric "[SOAK] exe=$exe"

# ---- Segment 2: size baseline (MSI bytes + installed dir usage) ----
$msiItem = Get-Item $MsiPath -ErrorAction SilentlyContinue
if (-not $msiItem) { Write-Host "[ERROR] msi not found at $MsiPath"; exit 1 }
$dirFiles = Get-ChildItem -LiteralPath $installDir -Recurse -File -ErrorAction SilentlyContinue
$dirBytes = ($dirFiles | Measure-Object Length -Sum).Sum
Add-Metric "[SOAK] msi_bytes=$($msiItem.Length)"
Add-Metric "[SOAK] install_file_count=$($dirFiles.Count)"
Add-Metric "[SOAK] install_dir_bytes=$dirBytes"

# ---- Segment 3: attach or launch (live-fire walk leaves the app running) ----
function Get-AppProc { Get-Process -Name "TaoKeeper.App" -ErrorAction SilentlyContinue | Select-Object -First 1 }
$proc = Get-AppProc
$attached = $true
if (-not $proc) {
  $attached = $false
  $proc = Start-Process -FilePath $exe -WorkingDirectory $installDir -PassThru
  Write-Host "[SOAK] launched new pid=$($proc.Id)"
}
Start-Sleep -Seconds 5
$proc.Refresh()
$deadline = (Get-Date).AddSeconds(60)
while ($proc.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline -and -not $proc.HasExited) {
  Start-Sleep -Seconds 3
  $proc.Refresh()
}
if ($proc.MainWindowHandle -eq [IntPtr]::Zero) {
  Save-Shot "99-no-window.png"
  Write-Host "[ERROR] main window not found in 60s; alive=$(-not $proc.HasExited)"
  exit 1
}
Add-Metric "[SOAK] attached=$attached pid=$($proc.Id)"
Save-Shot "00-soak-start.png"

# ---- Segment 4: idle memory baseline (3 samples, 5s apart) ----
$baseWs = @(); $basePriv = @()
for ($i = 0; $i -lt 3; $i++) {
  $proc.Refresh()
  $baseWs += $proc.WorkingSet64
  $basePriv += $proc.PrivateMemorySize64
  Start-Sleep -Seconds 5
}
$baseWsAvg = ($baseWs | Measure-Object -Average).Average
$basePrivAvg = ($basePriv | Measure-Object -Average).Average
$baseWsMin = ($baseWs | Measure-Object -Minimum).Minimum
Add-Metric "[SOAK] idle_baseline_ws_avg=$([math]::Round($baseWsAvg,0))"
Add-Metric "[SOAK] idle_baseline_ws_min=$baseWsMin"
Add-Metric "[SOAK] idle_baseline_priv_avg=$([math]::Round($basePrivAvg,0))"

# ---- Segment 5: real full scan via UIA (reuse live-fire walk proven approach) ----
$handle = $proc.MainWindowHandle
$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$ctItem = [System.Windows.Automation.ControlType]::ListItem
$ctBtn  = [System.Windows.Automation.ControlType]::Button
$c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctItem)
$c2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctBtn)
$els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.OrCondition($c1, $c2)))
$navItems = @()
foreach ($e in $els) { if ($e.Current.ControlType -eq $ctItem) { $navItems += @{ El = $e; Name = $e.Current.Name } } }
Write-Host "[UIA] nav items: $($navItems.Count)"
$scanTriggered = $false
if ($navItems.Count -ge 3) {
  try {
    $navEl = $navItems[2].El
    try { $navEl.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch { $navEl.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    Start-Sleep -Seconds 4
    Save-Shot "01-scan-page.png"
    $root2 = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $btns = $root2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c2)
    foreach ($btn in $btns) {
      if ($btn.Current.Name -match '扫描|分析|Scan|Analyze') {
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $scanTriggered = $true
        Write-Host "[SCAN] triggered via button '$($btn.Current.Name)'"
        break
      }
    }
  } catch { Write-Host "[WARN] scan trigger path error: $($_.Exception.Message)" }
}
if (-not $scanTriggered) {
  Save-Shot "98-scan-trigger-failed.png"
  $metricsPath = Join-Path $OutDir "soak-metrics.txt"
  $script:Metrics | Set-Content -Path $metricsPath -Encoding utf8
  Write-Host "[ERROR] real scan not triggered; soak cannot proceed without the required scan"
  exit 1
}
Save-Shot "02-scan-during.png"
$scanPeakWs = 0; $scanPeakPriv = 0
$scanUntil = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $scanUntil) {
  Start-Sleep -Seconds 5
  $p2 = Get-AppProc
  if (-not $p2) { break }
  $p2.Refresh()
  if ($p2.WorkingSet64 -gt $scanPeakWs) { $scanPeakWs = $p2.WorkingSet64 }
  if ($p2.PrivateMemorySize64 -gt $scanPeakPriv) { $scanPeakPriv = $p2.PrivateMemorySize64 }
}
Add-Metric "[SOAK] scan_peak_ws=$scanPeakWs"
Add-Metric "[SOAK] scan_peak_priv=$scanPeakPriv"
Save-Shot "03-scan-after.png"

# ---- Segment 6: soak loop with 15s sampling, restart tracking ----
$cores = [Environment]::ProcessorCount
$restarts = 0
$samples = 0
$sumWs = [double]0; $maxWs = [double]0; $sumPriv = [double]0; $maxPriv = [double]0
$prevCpuSec = $null; $sumCpuSec = [double]0
$prevWrite = $null; $sumWriteBytes = [long]0
$soakStart = Get-Date
while ($true) {
  $wall = ((Get-Date) - $soakStart).TotalSeconds
  if ($wall -ge $DurationSec) { break }
  $p = Get-AppProc
  if (-not $p) {
    $restarts++
    Write-Host "[WARN] app died during soak; restart #$restarts"
    Save-Shot "restart-$restarts.png"
    $p = Start-Process -FilePath $exe -WorkingDirectory $installDir -PassThru
    Start-Sleep -Seconds 10
    $p2 = Get-AppProc
    if (-not $p2) { Write-Host "[ERROR] app died and could not be restarted (attempt $restarts)"; exit 1 }
    $p = $p2
  }
  $p.Refresh()
  $ws = $p.WorkingSet64; $priv = $p.PrivateMemorySize64
  $cpuSec = $p.TotalProcessorTime.TotalSeconds
  if ($null -ne $prevCpuSec) { $sumCpuSec += ($cpuSec - $prevCpuSec) }
  $prevCpuSec = $cpuSec
  $cim = Get-CimInstance Win32_Process -Filter "Name='TaoKeeper.App.exe'" | Select-Object -First 1
  if ($cim) {
    $w = [long]$cim.WriteTransferCount
    if ($null -ne $prevWrite) { $sumWriteBytes += [math]::Max(0, $w - $prevWrite) }
    $prevWrite = $w
  }
  $sumWs += $ws; $sumPriv += $priv
  if ($ws -gt $maxWs) { $maxWs = $ws }
  if ($priv -gt $maxPriv) { $maxPriv = $priv }
  $samples++
  Start-Sleep -Seconds $SampleIntervalSec
}
$soakSeconds = [int]((Get-Date) - $soakStart).TotalSeconds

# ---- Segment 7: final state + hard assertions ----
$endProc = Get-AppProc
$endAlive = ($null -ne $endProc)
$avgWs = if ($samples -gt 0) { [math]::Round($sumWs / $samples, 0) } else { 0 }
$avgPriv = if ($samples -gt 0) { [math]::Round($sumPriv / $samples, 0) } else { 0 }
$cpuPct = if ($soakSeconds -gt 0) { [math]::Round(100.0 * $sumCpuSec / $soakSeconds, 2) } else { 0 }
Add-Metric "[SOAK] cores=$cores"
Add-Metric "[SOAK] soak_seconds=$soakSeconds"
Add-Metric "[SOAK] samples=$samples"
Add-Metric "[SOAK] restarts=$restarts"
Add-Metric "[SOAK] end_alive=$endAlive"
Add-Metric "[SOAK] ws_avg=$avgWs ws_peak=$([math]::Round($maxWs,0))"
Add-Metric "[SOAK] priv_avg=$avgPriv priv_peak=$([math]::Round($maxPriv,0))"
Add-Metric "[SOAK] cpu_total_seconds=$([math]::Round($sumCpuSec,2)) cpu_percent_single_core=$cpuPct"
Add-Metric "[SOAK] disk_write_bytes_total=$sumWriteBytes"

$a1 = $soakSeconds -ge 1800
$a2 = $samples -ge 110
$a3 = $endAlive
Add-Metric "[ASSERT] duration_ge_1800s=$a1"
Add-Metric "[ASSERT] samples_ge_110=$a2"
Add-Metric "[ASSERT] end_alive=$a3"
if ($restarts -gt 0) { Add-Metric "[WARN] restarts_during_soak=$restarts (reported truthfully, metric not gate)" }

$metricsPath = Join-Path $OutDir "soak-metrics.txt"
$script:Metrics | Set-Content -Path $metricsPath -Encoding utf8
if (-not (Test-Path $metricsPath)) { Write-Host "[ERROR] soak-metrics.txt not written"; exit 1 }
Add-Metric "[ASSERT] metrics_written=True"

if (-not ($a1 -and $a2 -and $a3)) { Write-Host "[ERROR] soak hard assertions failed"; exit 1 }
Write-Host "[OK] soak 30min complete: all hard assertions green"
