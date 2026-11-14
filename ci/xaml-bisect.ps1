#!/usr/bin/env pwsh
# XamlCompiler 崩溃二分（修红三）：pass1 硬崩无 XLS 输出，靠逐变体构建定位崩溃文件/片段。
# 每个变体：替换 XAML -> 清 obj/bin -> dotnet build -> 记录结果 -> 还原文件。全跑完打汇总表。
param(
    [string]$ProjDir = "src/TaoKeeper.App",
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)
$ErrorActionPreference = 'Continue'

$Widget = Join-Path $ProjDir "Views/DesktopWidgetWindow.xaml"
$SwPage = Join-Path $ProjDir "Views/SoftwarePage.xaml"
$VDir = "ci/xaml-bisect/variants"

function Clean-Obj {
    Remove-Item -Recurse -Force (Join-Path $ProjDir "obj") -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $ProjDir "bin") -ErrorAction SilentlyContinue
}

function Build-Once([string]$label) {
    Clean-Obj
    ci/uncomment-winappsdk.ps1 -CsprojPath (Join-Path $ProjDir "TaoKeeper.App.csproj") | Out-Null
    $log = "bisect-$label.log"
    dotnet build (Join-Path $ProjDir "TaoKeeper.App.csproj") -c $Configuration -p:Platform=$Platform 2>&1 | Tee-Object -FilePath $log | Out-Null
    $exit = $LASTEXITCODE
    ci/restore-winappsdk.ps1 -CsprojPath (Join-Path $ProjDir "TaoKeeper.App.csproj") | Out-Null
    $xc = (Select-String -Path $log -Pattern 'MSB3073' | Measure-Object).Count
    $xls = (Select-String -Path $log -Pattern 'XLS\d+' | Measure-Object).Count
    $result = if ($exit -eq 0) { "GREEN" } else { "RED(xc=$xc xls=$xls)" }
    Write-Host "[BISECT] $label -> exit=$exit $result"
    if ($xls -gt 0) { Select-String -Path $log -Pattern 'XLS\d+' | Select-Object -First 10 | ForEach-Object { Write-Host ("  " + $_.Line) } }
    return ,@($label, $exit, $result)
}

$results = @()
git checkout -- $Widget $SwPage 2>$null

# v0 对照：当前全量（预期红）
$results += Build-Once "v0-full-current"

# v1：挂件窗换最小壳（保留 x:Class 与根 Grid），SoftwarePage 原样
Copy-Item "$VDir/widget-min.xaml" $Widget -Force
$results += Build-Once "v1-widget-min"

# v2：挂件窗完整但去 ContextFlyout 菜单
Copy-Item "$VDir/widget-noflyout.xaml" $Widget -Force
$results += Build-Once "v2-widget-noflyout"

# v3：挂件窗完整但去确认浮层
Copy-Item "$VDir/widget-nolayer.xaml" $Widget -Force
$results += Build-Once "v3-widget-nolayer"

# v4：SoftwarePage 回退 main 版（a6c4e15），挂件窗还原完整
git checkout -- $Widget
git show a6c4e15:src/TaoKeeper.App/Views/SoftwarePage.xaml | Set-Content $SwPage -Encoding UTF8
$results += Build-Once "v4-swpage-main"

# 还原
git checkout -- $Widget $SwPage
Clean-Obj

Write-Host "===== BISECT SUMMARY ====="
$results | ForEach-Object { Write-Host ("{0,-24} exit={1,-4} {2}" -f $_[0], $_[1], $_[2]) }
Write-Host "[BISECT] 红=绿组合交叉即定位：v1/v2/v3 谁变绿=挂件窗对应片段；v4 变绿=SoftwarePage。"
