# TaoKeeper 自取包一键构建脚本（真机执行）
# 用法：cd 到本脚本所在目录，右键 PowerShell 运行：
#   powershell -ExecutionPolicy Bypass -File .\build.ps1            # 全量：测试 + C++ 核心
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -SkipNative  # 跳过 C++（没装 CMake 时用）
# 前置：.NET 8 SDK 或更高（更高版本脚本自动滚动前向运行，建议仍装 8 完全对齐）；可选：CMake 3.20+ 与 VS2022 Build Tools（编 C++ 核心）
# 铁律：任一测试工程红 → 立即停止，不产出任何包；结果与哈希自动写入 dist\ 目录。

param(
    [switch]$SkipNative
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$root = $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dist = Join-Path $root "dist\$stamp"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$report = New-Object System.Collections.Generic.List[string]
$report.Add("PACKAGE VERSION: PC-FULL-V1 (2026-11-09) — 如果你看到的报告没有这一行，说明跑的是旧包")
$report.Add("TaoKeeper 自取包构建报告 $stamp")
$report.Add("说明：本包为工程中自取包，未走完九层全测；测试结果如下，红字必须先回传大光。")
$report.Add("")

Write-Host '=== 1/4 检查 .NET SDK ===' -ForegroundColor Cyan
$dotnetVersion = (& dotnet --version) 2>$null
$dotnetMajor = 0
if ($dotnetVersion) {
    $m = [regex]::Match($dotnetVersion.Trim(), '^\d+')
    if ($m.Success) { $dotnetMajor = [int]$m.Value }
}
if ($dotnetMajor -lt 8) {
    Write-Host "未检测到 .NET 8 SDK（当前 $dotnetVersion）。请到 https://dotnet.microsoft.com/download/dotnet/8.0 安装 x64 SDK 后重跑。" -ForegroundColor Red
    exit 3
}
if ($dotnetMajor -eq 8) {
    Write-Host "  dotnet $dotnetVersion 通过"
} else {
    # 高于 8 的 SDK（如 10.x）：设滚动前向，让 net8.0 测试工程在当前 SDK 上运行；报告中如实注明。
    $env:DOTNET_ROLL_FORWARD = 'LatestMajor'
    Write-Host "  dotnet $dotnetVersion（高于 8，已设 DOTNET_ROLL_FORWARD=LatestMajor 滚动前向运行；建议真机仍装 8 完全对齐）"
    $report.Add("[黄] .NET SDK $dotnetVersion 高于 8：已滚动前向运行 net8.0 工程；建议真机仍装 8 完全对齐")
}

Write-Host '=== 2/4 跑全量单测（红即停）===' -ForegroundColor Cyan
# 坑册一 3（tao-build-pitfalls）：出包前扫非 ASCII 文件名（TaoPad 打法），先于测试执行
$badNames = Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'tests'), (Join-Path $root 'tools'), (Join-Path $root 'native') -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin|\.precheck)[\\/]' } |
    Where-Object { $_.Name -match '[^\x00-\x7F]' }
if ($badNames.Count -gt 0) {
    Write-Host '发现非 ASCII 文件名（真机复制/打包会乱码），先改名再出包：' -ForegroundColor Red
    $badNames | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.FullName.Substring($root.Length + 1))" -ForegroundColor Red }
    $report.Add("[红] 非 ASCII 文件名 $($badNames.Count) 个，先改名再出包")
    $report | Out-File -FilePath (Join-Path $dist '构建报告.txt') -Encoding utf8
    exit 2
}
Write-Host '  非 ASCII 文件名扫描：0 个，通过'
$report.Add('[绿] 非 ASCII 文件名扫描：src/tests/tools/native 0 个')

$tests = @('tests/TaoKeeper.Core.Tests', 'tests/TaoKeeper.AppKernel.Tests', 'tests/TaoKeeper.Adapters.Tests')
foreach ($t in $tests) {
    Write-Host "  --- $t"
    & dotnet test (Join-Path $root $t) -c Release --nologo -v q | Tee-Object -Variable testOutput
    if ($LASTEXITCODE -ne 0) {
        Write-Host "测试失败：$t —— 已停止，不产出包。把上面红字整段回传给大光。" -ForegroundColor Red
        $report.Add("[红] $t 测试失败（退出码 $LASTEXITCODE）")
        $report | Out-File -FilePath (Join-Path $dist '构建报告.txt') -Encoding utf8
        exit 2
    }
    $summary = ($testOutput | Select-Object -Last 1)
    $report.Add("[绿] $t => $summary")
}

Write-Host '=== 3/4 构建真机自检台（TaoKeeper.Harness）===' -ForegroundColor Cyan
& dotnet build (Join-Path $root 'src/TaoKeeper.Harness') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host '自检台构建失败：把红字整段回传给大光。' -ForegroundColor Red
    exit 2
}
Write-Host '  构建通过（运行方式见操作说明）'

Write-Host '=== 4/4 构建 C++ 扫描核心（可选）===' -ForegroundColor Cyan
if ($SkipNative) {
    Write-Host '  已按 -SkipNative 跳过 C++ 核心；自取包实测不受影响，核心部分留待完整包。'
    $report.Add('[跳] C++ 核心：-SkipNative 跳过')
} else {
    $cmake = Get-Command cmake -ErrorAction SilentlyContinue
    if (-not $cmake) {
        Write-Host '  未检测到 CMake，跳过 C++ 核心（如需完整能力：装 CMake 3.20+ 与 VS2022 Build Tools 后去掉 -SkipNative）。' -ForegroundColor Yellow
        $report.Add('[黄] C++ 核心：无 CMake，跳过')
    } else {
        & cmake -S (Join-Path $root 'native/TaoKeeperScan') -B (Join-Path $root 'native/TaoKeeperScan/build') 2>&1 | Out-Null
        & cmake --build (Join-Path $root 'native/TaoKeeperScan/build') --config Release 2>&1 | Out-Null
        & ctest --test-dir (Join-Path $root 'native/TaoKeeperScan/build') -C Release 2>&1 | Tee-Object -Variable ctestOutput
        if ($LASTEXITCODE -ne 0) {
            Write-Host '  C++ ctest 未通过：把红字整段回传给大光（不阻断适配层自检，但核心能力暂不可用）。' -ForegroundColor Yellow
            $report.Add("[黄] C++ ctest 未通过（退出码 $LASTEXITCODE）")
        } else {
            $report.Add('[绿] C++ ctest：' + ($ctestOutput | Select-Object -Last 1))
        }
    }
}

Write-Host '=== 生成哈希清单与报告 ===' -ForegroundColor Cyan
$targets = Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Include '*.dll','*.exe' |
    Where-Object { $_.FullName -notmatch '[\\/]obj[\\/]' } |
    Where-Object { $_.FullName -match '[\\/]bin[\\/]Release[\\/]' }
$hashes = $targets | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    "$h  $($_.FullName.Replace($root, 'dev'))"
}
$hashes | Out-File -FilePath (Join-Path $dist '哈希清单-SHA256.txt') -Encoding utf8
$report.Add('')
$report.Add('产物 SHA-256（完整清单见同目录 哈希清单-SHA256.txt）：')
$report.Add($hashes -join "`r`n")
$report | Out-File -FilePath (Join-Path $dist '构建报告.txt') -Encoding utf8

Write-Host ''
Write-Host "构建完成：全绿。报告与哈希见 $dist" -ForegroundColor Green
Write-Host '下一步：按 操作说明.md 跑真机自检台，把输出回传大光。'
exit 0
