#!/usr/bin/env pwsh
# XamlCompiler 取证（修红三）：MSB3073 只报退出码，真实错误全在 output.json，两轮红灯都没打到 stdout。
# 本脚本在构建后调用：解析 XamlCompiler 的 output.json/input.json，把 XLS 真实错误与输入摘要打上 stdout。
param(
    [string]$ProjDir = "src/TaoKeeper.App",
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = 'Continue'
$target = "$((Get-Item $ProjDir).FullName)/obj/$Platform/$Configuration/net8.0-windows10.0.22621.0"
$out = Join-Path $target 'output.json'
$inp = Join-Path $target 'input.json'

Write-Host "===== XamlCompiler evidence: $target ====="
if (Test-Path $out) {
    Write-Host "----- output.json (raw) -----"
    Get-Content $out -Raw | Write-Host
    try {
        $j = Get-Content $out -Raw | ConvertFrom-Json
        $errs = @()
        foreach ($p in @('Errors','Error','Diagnostics','Messages')) {
            $v = $j.$p
            if ($v) { $errs += @($v) }
        }
        if ($errs.Count -gt 0) {
            Write-Host "----- parsed diagnostics ($($errs.Count)) -----"
            $errs | ForEach-Object { Write-Host $_ | ConvertTo-Json -Depth 6 -Compress }
        }
    } catch { Write-Host "[evidence] output.json 解析失败: $_" }
} else {
    Write-Host "[evidence] output.json 不存在: $out"
}

if (Test-Path $inp) {
    try {
        $ji = Get-Content $inp -Raw | ConvertFrom-Json
        Write-Host "----- input.json 摘要 -----"
        $ji.PSObject.Properties | ForEach-Object {
            $v = $_.Value
            if ($v -is [string] -and $v.Length -gt 200) { $v = $v.Substring(0,200) + "..." }
            elseif ($v -isnot [string] -and $v -isnot [int] -and $v -isnot [bool]) { $v = ($v | ConvertTo-Json -Depth 3 -Compress) }
            Write-Host "$($_.Name) = $v"
        }
    } catch { Write-Host "[evidence] input.json 解析失败: $_" }
} else {
    Write-Host "[evidence] input.json 不存在: $inp"
}

# 兜底：从 winui-build.log 里捞 XLS 行与 XamlCompiler 段落
if (Test-Path "winui-build.log") {
    Write-Host "----- winui-build.log 中 XLS/XamlCompiler 相关行 -----"
    Select-String -Path winui-build.log -Pattern 'XLS\d+|XamlCompiler|\.xaml' |
        Select-Object -First 40 | ForEach-Object { Write-Host $_.Line }
}

# 产物留档：两个 json 直接上传 artifact（workflow 用 actions/upload-artifact 收）
if (Test-Path $out)  { Copy-Item $out "$PSScriptRoot/../xaml-compiler-output.json" -Force; Write-Host "[evidence] 已复制 output.json" }
if (Test-Path $inp)  { Copy-Item $inp "$PSScriptRoot/../xaml-compiler-input.json" -Force; Write-Host "[evidence] 已复制 input.json" }
