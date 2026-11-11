# TaoKeeper CI: launch installed app, UIA-walk every page, screenshot each (visual evidence)
# CI-only script (boundary: no edits to app code). ASCII-only, UTF-8 BOM per pitfall list.
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
  Write-Host "[ERROR] main window not found in 60s; process alive=$(-not $p.HasExited)"
  if ($p.HasExited) { Write-Host "[ERROR] process exited code=$($p.ExitCode)" }
  exit 1
}
Start-Sleep -Seconds 5
Save-Shot "01-main-window.png"
Write-Host "[OK] window found, initial shot saved"

$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
Write-Host "[UIA] root: $($root.Current.Name)"

$ctItem = [System.Windows.Automation.ControlType]::ListItem
$ctBtn  = [System.Windows.Automation.ControlType]::Button
$ctMenu = [System.Windows.Automation.ControlType]::MenuItem
$c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctItem)
$c2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctBtn)
$c3 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctMenu)
$cond = New-Object System.Windows.Automation.OrCondition((New-Object System.Windows.Automation.OrCondition($c1, $c2)), $c3)
$els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Host "[UIA] candidates: $($els.Count)"

$seen = @{}
$idx = 0
foreach ($el in $els) {
  $name = ''
  $aid  = ''
  try { $name = $el.Current.Name } catch { }
  try { $aid  = $el.Current.AutomationId } catch { }
  if ([string]::IsNullOrWhiteSpace($name) -and [string]::IsNullOrWhiteSpace($aid)) { continue }
  if ($aid -match 'Close|Minimize|Maximize|Exit' -or $name -match '^(Close|Minimi[sz]e|Maximize|Exit)') { continue }
  $key = "$name|$aid"
  if ($seen.ContainsKey($key)) { continue } else { $seen[$key] = $true }
  if ($seen.Count -gt 30) { break }
  $idx++
  $safe = ($name -replace '[^\w\-]', '_')
  if (-not $safe) { $safe = 'unnamed' }
  try {
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  } catch {
    try {
      $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } catch { Write-Host "[UIA-SKIP] $name (no invoke/selection pattern)"; continue }
  }
  Start-Sleep -Seconds 3
  Save-Shot ("page-{0:D2}-{1}.png" -f $idx, $safe)
  Write-Host "[UIA-WALK] $name -> shot saved"
  if ($p.HasExited) { Write-Host "[ERROR] app exited during walk at $name"; exit 1 }
}
Save-Shot "98-final.png"
Write-Host "[OK] ui walk complete: $idx pages shot"
