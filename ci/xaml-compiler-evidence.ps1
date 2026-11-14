#!/usr/bin/env pwsh
# XamlCompiler 取证 v2（修红三）：WindowsAppSDK 1.5 已知缺陷（GitHub #9813）——dotnet build 下
# XAML 编译错误被静默吞掉，只报 MSB3073 exited with code 1。真实错误只能从 output.json 或
# 编译器直跑控制台输出抢救。v1 教训：单一固定路径找不到 output.json；input.json 巨型属性
# ConvertTo-Json 卡死步骤。v2 打法：递归搜全树 + 直跑编译器抓控制台 + 全程限时，永不卡死。
param(
    [string]$ProjDir = "src/TaoKeeper.App",
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = 'Continue'
$log = "xaml-evidence.log"
function Out-Both([string]$line) {
    Write-Host $line
    Add-Content -Path $log -Value $line -ErrorAction SilentlyContinue
}

$projFull = (Get-Item $ProjDir).FullName
Out-Both "===== XamlCompiler evidence v2 @ $projFull ====="

# 1) 递归搜 obj 全树（含 win-x64 子目录变体），不限单一固定路径
$jsons = Get-ChildItem -Path $projFull -Recurse -Filter "*.json" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('input.json','output.json') -and $_.FullName -match 'obj' } |
    Sort-Object FullName
if (-not $jsons) {
    Out-Both "[evidence] obj 下未发现 input/output.json，转搜 TEMP 与项目根"
    $jsons = Get-ChildItem -Path $env:TEMP, $projFull -Recurse -Filter "output.json" -ErrorAction SilentlyContinue | Select-Object -First 5
}
foreach ($f in $jsons) {
    $kind = if ($f.Name -eq 'output.json') { 'OUTPUT' } else { 'INPUT' }
    Out-Both "----- $kind : $($f.FullName) (size=$($f.Length)) -----"
    if ($f.Name -eq 'output.json') {
        # output.json 是唯一可能藏真实错误的地方，全文打印（限 400 行防爆）
        Get-Content $f.FullName -TotalCount 400 | ForEach-Object { Out-Both $_ }
    }
}

# 2) 直跑 XamlCompiler：用最新 input.json 重放，控制台输出 + 独立 output 文件双通道抓错
$xamlComp = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windowsappsdk" -Recurse -Filter "XamlCompiler.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'net472|net6.0' } | Sort-Object FullName -Descending | Select-Object -First 1
$replayInput = $jsons | Where-Object Name -eq 'input.json' | Select-Object -First 1
if ($xamlComp -and $replayInput) {
    $replayOut = Join-Path $env:TEMP "replay-output-$PID.json"
    Out-Both "----- 直跑重放开始 -----"
    Out-Both "[evidence] compiler=$($xamlComp.FullName)"
    Out-Both "[evidence] input=$($replayInput.FullName)"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $xamlComp.FullName -ArgumentList @('"'+$replayInput.FullName+'"','"'+$replayOut+'"') -NoNewWindow -PassThru -RedirectStandardError "$env:TEMP\replay-err-$PID.txt" -RedirectStandardOutput "$env:TEMP\replay-out-$PID.txt"
    if (-not $proc.WaitForExit(120000)) { $proc.Kill(); Out-Both "[evidence] 重放超时 120s 已终止" }
    $sw.Stop()
    Out-Both "[evidence] 重放退出码=$($proc.ExitCode) 耗时=$($sw.ElapsedMilliseconds)ms"
    foreach ($p in @("$env:TEMP\replay-out-$PID.txt", "$env:TEMP\replay-err-$PID.txt")) {
        if (Test-Path $p) {
            $c = Get-Content $p -ErrorAction SilentlyContinue
            if ($c) { Out-Both "----- 重放 $([IO.Path]::GetFileName($p)) -----"; $c | Select-Object -First 200 | ForEach-Object { Out-Both $_ } }
        }
    }
    if (Test-Path $replayOut) {
        Out-Both "----- 重放 output.json（真实错误主通道）-----"
        Get-Content $replayOut -TotalCount 400 | ForEach-Object { Out-Both $_ }
        Copy-Item $replayOut "replay-output.json" -Force -ErrorAction SilentlyContinue
    }
} else {
    Out-Both "[evidence] 直跑重放跳过：compiler=$([bool]$xamlComp) input=$([bool]$replayInput)"
}

# 3) 兜底：winui-build.log 里捞 XLS 行
if (Test-Path "winui-build.log") {
    $hits = Select-String -Path "winui-build.log" -Pattern 'XLS\d+' | Select-Object -First 20
    if ($hits) { Out-Both "----- winui-build.log XLS 行 -----"; $hits | ForEach-Object { Out-Both $_.Line } }
    else { Out-Both "[evidence] winui-build.log 无 XLS 行（与 #9813 吞错症状一致）" }
}

Out-Both "===== evidence v2 end ====="
