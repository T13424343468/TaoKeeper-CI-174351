#!/usr/bin/env pwsh
# XamlCompiler 崩溃二分 v2（修红三）：pass1 硬崩无 XLS 输出、无 output.json。
# 打法：按组隔离——整组还原到 origin/main，只留一组改动，构建记录结果；红=该组含崩溃源。
# 每轮构建清 obj/bin；构建日志落盘 bisect-<组>.log，末尾汇总并上传 artifact。
param(
    [string]$ProjDir = "src/TaoKeeper.App",
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)
$ErrorActionPreference = 'Continue'
$csproj = Join-Path $ProjDir "TaoKeeper.App.csproj"

# 确保能取到 origin/main（actions/checkout 默认浅克隆仅当前分支）
git fetch origin main --depth=1 2>$null

# 修错批相对 origin/main 的全部改动文件
$batchFiles = git diff --name-only origin/main...HEAD | Where-Object { $_ -notmatch '^ci/|^.github/' }
Write-Host "[BISECT] batch files: $($batchFiles -join ', ')"

function Restore-All {
    git checkout origin/main -- @($batchFiles) 2>$null
    git clean -f @($batchFiles) 2>$null | Out-Null
}
function Clean-Obj {
    Remove-Item -Recurse -Force (Join-Path $ProjDir "obj") -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $ProjDir "bin") -ErrorAction SilentlyContinue
}
function Build-Once([string]$label, [string[]]$keep) {
    Restore-All
    foreach ($f in $keep) { git checkout HEAD -- $f 2>$null; if (-not (Test-Path $f)) { Write-Host "[BISECT][WARN] 缺文件 $f" } }
    Clean-Obj
    ci/uncomment-winappsdk.ps1 -CsprojPath $csproj | Out-Null
    $log = "bisect-$label.log"
    dotnet build $csproj -c $Configuration -p:Platform=$Platform 2>&1 | Tee-Object -FilePath $log | Out-Null
    $exit = $LASTEXITCODE
    ci/restore-winappsdk.ps1 -CsprojPath $csproj | Out-Null
    $result = if ($exit -eq 0) { "GREEN" } else {
        $errs = (Select-String -Path $log -Pattern ': error ' | Select-Object -First 6)
        "RED"
    }
    Write-Host "[BISECT] $label -> exit=$exit $result"
    if ($exit -ne 0) {
        Select-String -Path $log -Pattern ': error |XLS\d+|XamlCompiler' | Select-Object -First 8 | ForEach-Object { Write-Host ("  >> " + $_.Line.Substring(0, [Math]::Min(400, $_.Line.Length))) }
    }
    return ,@($label, $exit, $result)
}

# 分组（互斥覆盖全部批改动）
$G_widget = @('src/TaoKeeper.App/Views/DesktopWidgetWindow.xaml','src/TaoKeeper.App/Views/DesktopWidgetWindow.xaml.cs')
$G_swpage = @('src/TaoKeeper.App/Views/SoftwarePage.xaml','src/TaoKeeper.App/Views/SoftwarePage.xaml.cs')
$G_text   = @('src/TaoKeeper.App/Views/SettingsPage.xaml','src/TaoKeeper.App/Views/SettingsPage.xaml.cs','src/TaoKeeper.App/Views/ToolsPage.xaml','src/TaoKeeper.App/Views/ToolsPage.xaml.cs','src/TaoKeeper.App/Views/WidgetPage.xaml','src/TaoKeeper.App/Views/WidgetPage.xaml.cs','src/TaoKeeper.App/PlaceholderPage.xaml.cs')
$G_wire   = @('src/TaoKeeper.App/App.xaml.cs','src/TaoKeeper.App/CompositionRoot.cs','src/TaoKeeper.App/MainWindow.xaml.cs','src/TaoKeeper.App/PageRegistry.cs','src/TaoKeeper.AppKernel/Navigation/AppRoute.cs','tests/TaoKeeper.AppKernel.Tests/AppKernelTests.cs')

$results = @()
$results += Build-Once "G0-all-batch"  ($batchFiles)
$results += Build-Once "G1-widget-only" $G_widget
$results += Build-Once "G2-swpage-only" $G_swpage
$results += Build-Once "G3-text-only"   $G_text
$results += Build-Once "G4-wire-only"   $G_wire
Restore-All
Clean-Obj

Write-Host "===== BISECT SUMMARY ====="
$results | ForEach-Object { Write-Host ("{0,-16} exit={1,-4} {2}" -f $_[0], $_[1], $_[2]) }
Write-Host "[BISECT] 判读：绿组=清白；红组=崩溃源在该组。G1/G2 互斥定位 XAML；全红但 G0 红=全局性问题再议。"
