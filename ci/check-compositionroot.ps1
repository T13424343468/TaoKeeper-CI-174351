# CI 闸门：组合根引用完整性预检（r11 修红回灌，2026-11-12）
# 背景：CompositionRoot.cs 引用 TaoKeeper.Adapters / TaoKeeper.Core.Models，但壳工程
# 缺工程引用与 using，沙箱不构建 WinUI 壳导致该类错误直到 CI 才暴露（r11 四连红根因）。
# 组合根以参数类型引用壳自身 WinUI 类型（MainWindow），无法纯 BCL 独立编译；
# 故本闸门用「反射装载三依赖程序集 + 静态核对」等效拦截同类错误：
#   A) using 的每个 TaoKeeper.* 命名空间必须存在于被引用依赖程序集（壳自身 TaoKeeper.App 除外）；
#   B) 命名空间归属工程必须出现在 App.csproj 的 ProjectReference（拦 CS0234：缺工程引用）；
#   C) 文中大写标识符若与依赖程序集导出类型名唯一匹配，其精确命名空间必须在 using 或以全限定名出现（拦 CS0246：缺 using）。
# 局限（如实声明）：组合根本身类型与 WinUI/壳类型不在三程序集内，不参与核对；
#   本闸门拦截「缺引用/缺 using」两类已踩过的坑，不等价于完整编译，最终以 CI 壳构建为准。
# 用法：pwsh ci/check-compositionroot.ps1（仓库根目录执行；Linux/Windows 均可跑）
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$comp = Join-Path $repo 'src/TaoKeeper.App/CompositionRoot.cs'
$appCsproj = Join-Path $repo 'src/TaoKeeper.App/TaoKeeper.App.csproj'
if (-not (Test-Path $comp)) { Write-Host '[CHECK-CR] CompositionRoot.cs not found'; exit 1 }

$projects = [ordered]@{
  'TaoKeeper.Core'     = 'src/TaoKeeper.Core/TaoKeeper.Core.csproj'
  'TaoKeeper.AppKernel' = 'src/TaoKeeper.AppKernel/TaoKeeper.AppKernel.csproj'
  'TaoKeeper.Adapters'  = 'src/TaoKeeper.Adapters/TaoKeeper.Adapters.csproj'
}

# 1) 依赖三工程先构建（零错零警闸）
foreach ($name in $projects.Keys) {
  $csproj = Join-Path $repo $projects[$name]
  & dotnet build $csproj -c Release --nologo 2>&1 | Tee-Object -Variable out | Out-Null
  $errs = ($out | Select-String -Pattern ': error ').Count
  $warns = ($out | Select-String -Pattern ': warning ').Count
  Write-Host "[CHECK-CR] build $name errors=$errs warnings=$warns"
  if ($errs -ne 0 -or $warns -ne 0) { exit 1 }
}

# 2) 反射装载，收集导出类型 (SimpleName -> 命名空间集合)
$bins = @($projects.Keys | ForEach-Object {
  $dir = Join-Path (Join-Path $repo (Split-Path -Parent $projects[$_])) 'bin/Release/net8.0'
  Get-ChildItem (Join-Path $dir 'TaoKeeper.*.dll') | Where-Object { $_.Name -notmatch '\.ref' } | Select-Object -First 1
})
$ev = [System.ResolveEventHandler] { param($s, $e)
  foreach ($b in $bins) {
    $p = Join-Path $b.DirectoryName ($e.Name.Split(',')[0] + '.dll')
    if (Test-Path $p) { return [System.Reflection.Assembly]::LoadFrom($p) }
  }
  $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($ev)
$typeMap = @{}   # simple name -> list of namespaces
$nsToProject = @{}
foreach ($dll in $bins) {
  $asm = [System.Reflection.Assembly]::LoadFrom($dll.FullName)
  $projName = ($projects.Keys | Where-Object { $dll.DirectoryName -like "*$_*" } | Select-Object -First 1)
  foreach ($t in $asm.GetTypes()) {
    if ($t.Namespace -and $t.IsPublic) {
      $nsToProject[$t.Namespace] = $projName
      if (-not $typeMap.ContainsKey($t.Name)) { $typeMap[$t.Name] = [System.Collections.Generic.List[string]]::new() }
      if (-not $typeMap[$t.Name].Contains($t.Namespace)) { $typeMap[$t.Name].Add($t.Namespace) }
    }
  }
}
Write-Host ("[CHECK-CR] loaded {0} assemblies, {1} public types, {2} namespaces" -f $bins.Count, ($typeMap.Keys.Count), $nsToProject.Keys.Count)

# 3) 解析组合根源码
$src = Get-Content $comp -Raw
# 去注释与字符串字面量，防误报
$clean = [regex]::Replace($src, '//[^\r\n]*', '')
$clean = [regex]::Replace($clean, '(?s)/\*.*?\*/', '')
$clean = [regex]::Replace($clean, '"(?:[^"\\]|\\.)*"', '""')
$usings = [regex]::Matches($clean, 'using\s+(TaoKeeper[\w.]*)\s*;') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

$fail = @()

# A) using 命名空间必须在依赖程序集中存在（壳自身 TaoKeeper.App 除外）
foreach ($ns in $usings) {
  if ($ns -eq 'TaoKeeper.App') { continue }
  $hit = $nsToProject.Keys | Where-Object { $_ -eq $ns -or $_ -like "$ns.*" -or $ns -like "$_.*" -or ($ns -split '\.')[0..1] -join '.' -eq ($_ -split '\.')[0..1] -join '.' }
  if (-not $hit) { $fail += "A: using $ns 在三个依赖程序集中不存在（缺引用或拼错）" }
}

# B) 命名空间归属工程必须在 App.csproj 的 ProjectReference 中
$csprojText = Get-Content $appCsproj -Raw
$refs = [regex]::Matches($csprojText, 'ProjectReference\s+Include="([^"]+)"') | ForEach-Object { Split-Path -Leaf $_.Groups[1].Value }
foreach ($ns in $usings) {
  if ($ns -eq 'TaoKeeper.App') { continue }
  $root = ($ns -split '\.')[0..1] -join '.'
  $owner = $null
  foreach ($k in $nsToProject.Keys) { if (($k -split '\.')[0..1] -join '.' -eq $root) { $owner = $nsToProject[$k]; break } }
  if ($owner) {
    $need = Split-Path -Leaf $projects[$owner]
    if ($refs -notcontains $need) { $fail += "B: using $ns 属于 $need，App.csproj 缺 ProjectReference（CS0234 类）" }
  }
}

# C) 大写标识符唯一匹配导出类型 -> 精确命名空间必须在 using 或全限定出现
$idents = [regex]::Matches($clean, '(?<![.\w])([A-Z][A-Za-z0-9_]*)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($id in $idents) {
  if (-not $typeMap.ContainsKey($id)) { continue }
  $nss = $typeMap[$id]
  if ($nss.Count -ne 1) { continue }   # 跨程序集同名，无法判界，跳过
  $ns = $nss[0]
  $covered = ($usings -contains $ns) -or ($clean -match [regex]::Escape("$ns.$id"))
  if (-not $covered) { $fail += "C: 类型 $id 位于 $ns，但未 using 且未全限定（CS0246 类）" }
}

if ($fail.Count -gt 0) {
  Write-Host '[CHECK-CR] FAIL:'
  $fail | ForEach-Object { Write-Host "  - $_" }
  exit 1
}
Write-Host '[CHECK-CR] PASS: 组合根 using/工程引用/类型命名空间三方一致'
