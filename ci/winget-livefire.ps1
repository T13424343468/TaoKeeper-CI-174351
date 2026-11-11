# TaoKeeper live-fire winget end-to-end: real install -> verify -> real uninstall
# -> verify gone. Package: 7zip.7zip (reason: small ~1.5MB MSI, zero runtime deps,
# official winget community source, silent install/uninstall both supported).
# CI-only script. ASCII-only, UTF-8 BOM.
param(
  [Parameter(Mandatory=$true)][string]$OutDir,
  [string]$PackageId = '7zip.7zip'
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:failCount = 0
function Write-Assert([string]$Step, [bool]$Ok, [string]$Reason) {
  $verdict = 'PASS'
  if (-not $Ok) { $verdict = 'FAIL'; $script:failCount++ }
  Write-Host ("[{0}] {1}: {2}" -f $verdict, $Step, $Reason)
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
function Test-Installed([string]$Id) {
  $out = & winget list --id $Id --exact --accept-source-agreements 2>&1 | Out-String
  return ($out -match [regex]::Escape($Id)) -and ($out -notmatch 'No installed found|找不到|没有')
}

# Step 0: winget present and source reachable.
try {
  $null = & winget --version 2>&1
  Write-Assert "winget:present" $true "winget version: $(& winget --version 2>&1)"
} catch {
  Write-Assert "winget:present" $false "winget not runnable: $($_.Exception.Message)"
  exit 1
}

# Step 1: real install.
Write-Host "[STEP] winget install --id $PackageId --silent"
$installOut = & winget install --id $PackageId --exact --silent --accept-package-agreements --accept-source-agreements 2>&1 | Out-String
Write-Host $installOut
$installOk = $LASTEXITCODE -eq 0
Save-Shot "01-install-done.png"
Write-Assert "winget:install" $installOk "exit=$LASTEXITCODE; package=$PackageId"
if (-not $installOk) { exit 1 }

# Step 2: verify installed.
Start-Sleep -Seconds 5
$afterInstall = Test-Installed $PackageId
Save-Shot "02-verify-installed.png"
Write-Assert "winget:verify-installed" $afterInstall "winget list finds $PackageId"

# Step 3: real uninstall.
Write-Host "[STEP] winget uninstall --id $PackageId --silent"
$uninstallOut = & winget uninstall --id $PackageId --exact --silent 2>&1 | Out-String
Write-Host $uninstallOut
$uninstallOk = $LASTEXITCODE -eq 0
Save-Shot "03-uninstall-done.png"
Write-Assert "winget:uninstall" $uninstallOk "exit=$LASTEXITCODE"

# Step 4: verify gone.
Start-Sleep -Seconds 5
$afterUninstall = -not (Test-Installed $PackageId)
Save-Shot "04-verify-gone.png"
Write-Assert "winget:verify-gone" $afterUninstall "winget list no longer shows $PackageId"

Write-Host "[SUMMARY] fails=$script:failCount"
if ($script:failCount -gt 0) { exit 1 }
Write-Host "[OK] winget live-fire complete: install/verify/uninstall/verify-gone all pass"
