# XamlCompiler MSB3073 fast bisect (backdesk, 2026-11-14)
# 单 runner 顺序变体构建定位崩溃源；前提：SDK 引用已 uncomment（pitfall 17）。
# V1 去挂件接线组 / V2 去软件库组 / V3 去文案组；每组整组还原 origin/main，build 后复位。
# 已知 43591361768 分支原样三连崩（=V0 RED），故 V0 对照省略，直接跑 V1-V3。
$ErrorActionPreference = 'Continue'
$proj = 'src/TaoKeeper.App/TaoKeeper.App.csproj'
$variants = [ordered]@{
  'V1-nowidget' = @(
    'src/TaoKeeper.App/App.xaml.cs',
    'src/TaoKeeper.App/CompositionRoot.cs',
    'src/TaoKeeper.App/MainWindow.xaml.cs',
    'src/TaoKeeper.App/PageRegistry.cs',
    'src/TaoKeeper.App/PlaceholderPage.xaml.cs',
    'src/TaoKeeper.App/Views/DesktopWidgetWindow.xaml',
    'src/TaoKeeper.App/Views/DesktopWidgetWindow.xaml.cs',
    'src/TaoKeeper.App/Views/WidgetPage.xaml.cs',
    'src/TaoKeeper.AppKernel/Navigation/AppRoute.cs'
  )
  'V2-nosoftlib' = @(
    'src/TaoKeeper.App/Views/SoftwarePage.xaml',
    'src/TaoKeeper.App/Views/SoftwarePage.xaml.cs'
  )
  'V3-copyedit' = @(
    'src/TaoKeeper.App/Views/SettingsPage.xaml',
    'src/TaoKeeper.App/Views/SettingsPage.xaml.cs',
    'src/TaoKeeper.App/Views/ToolsPage.xaml',
    'src/TaoKeeper.App/Views/ToolsPage.xaml.cs',
    'src/TaoKeeper.App/Views/WidgetPage.xaml'
  )
}
$results = @()
foreach ($k in $variants.Keys) {
  Write-Host "===== [BISECT] variant $k : restore main for group, clean obj, build ====="
  git checkout origin/main -- $variants[$k] 2>&1 | Out-Host
  if ($LASTEXITCODE -ne 0) { Write-Host "[BISECT] $k git restore FAILED"; $results += "[BISECT] $k = RESTORE-FAIL"; continue }
  Remove-Item -Recurse -Force src/TaoKeeper.App/obj -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force src/TaoKeeper.App/bin -ErrorAction SilentlyContinue
  $env:MSBUILDDISABLENODEREUSE = '1'
  dotnet build $proj -c Release -p:Platform=x64 -p:NodeReuse=false 2>&1 | Tee-Object -FilePath "bisect-$k.log" | Select-String -Pattern ': error ' | Out-Host
  $ok = ($LASTEXITCODE -eq 0)
  $results += "[BISECT] $k = " + $(if ($ok) { 'GREEN' } else { 'RED' })
  Write-Host $results[-1]
  git checkout HEAD -- $variants[$k] 2>&1 | Out-Host
  if ($ok) { break }  # 已定位：该组即崩溃源，停
}
$results | Out-Host
$results | Set-Content -FilePath bisect-result.txt
Write-Host "[BISECT] matrix done"
exit 0
