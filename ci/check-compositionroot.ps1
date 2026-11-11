# CI 闸门 v2：组合根引用完整性预检（r11/r13 两轮修红回灌，2026-11-12）
# 背景：沙箱不构建 WinUI 壳，组合根的引用错误直到 CI 才暴露——
#   r11：缺依赖工程引用（CS0234）+ 缺 using（CS0246）；r13：壳自身页类型裸引用缺 using（CS0103）。
# 组合根以参数类型引用壳自身 WinUI 类型（MainWindow/页面类），无法纯 BCL 独立编译；
# 故本闸门用「反射装载依赖程序集 + 壳源码类型清单 + 静态核对」等效拦截同类错误：
#   A) using 的每个 TaoKeeper.* 命名空间必须存在于依赖程序集或壳自身清单；
#   B) 命名空间属依赖工程时必须出现在 App.csproj 的 ProjectReference（拦 CS0234）；
#   C) 文中大写标识符若与「依赖程序集或壳自身」导出类型名唯一匹配，其精确命名空间必须在
#      using、或与组合根同命名空间（壳内直呼）、或以全限定名出现（拦 CS0246/CS0103）。
# 局限（如实声明）：本闸门拦「缺引用/缺 using」两类已踩过的坑，不核成员签名（成员级以
#   CI 壳构建为准）；不等于完整编译。
# 用法：pwsh ci/check-compositionroot.ps1（仓库根目录执行；Linux/Windows 均可跑）
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$comp = Join-Path $repo 'src/TaoKeeper.App/CompositionRoot.cs'
$appCsproj = Join-Path $repo 'src/TaoKeeper.App/TaoKeeper.App.csproj'
if (-not (Test-Path $comp)) { Write-Host '[CHECK-CR] CompositionRoot.cs not found'; exit 1 }

$projects = [ordered]@{
  'TaoKeeper.Core'      = 'src/TaoKeeper.Core/TaoKeeper.Core.csproj'
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

# 2) 壳自身类型清单（namespace -> type names，来自 src/TaoKeeper.App/**/*.cs，排除 obj/bin）
$appTypes = @{}   # simple name -> namespace
$appNs = New-Object System.Collections.Generic.HashSet[string]
Get-ChildItem (Join-Path $repo 'src/TaoKeeper.App') -Recurse -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
  ForEach-Object {
    $text = Get-Content $_.FullName -Raw
    $nsMatch = [regex]::Match($text, 'namespace\s+([\w.]+)\s*;')
    if (-not $nsMatch.Success) { $nsMatch = [regex]::Match($text, 'namespace\s+([\w.]+)\s*\r?\n') }
    if (-not $nsMatch.Success) { return }
    $ns = $nsMatch.Groups[1].Value
    [void]$appNs.Add($ns)
    foreach ($m in [regex]::Matches($text, '(?:class|record|struct|enum|interface)\s+([A-Z]\w*)')) {
      $appTypes[$m.Groups[1].Value] = $ns
    }
  }
Write-Host ("[CHECK-CR] app inventory: {0} types, {1} namespaces" -f $appTypes.Keys.Count, $appNs.Count)

# 3) 反射装载依赖程序集，收集导出类型 (SimpleName -> 命名空间集合)
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

# 4) 解析组合根源码（去注释与字符串字面量，防误报）
$src = Get-Content $comp -Raw
$clean = [regex]::Replace($src, '//[^\r\n]*', '')
$clean = [regex]::Replace($clean, '(?s)/\*.*?\*/', '')
$clean = [regex]::Replace($clean, '"(?:[^"\\]|\\.)*"', '""')
$ownNs = [regex]::Match($clean, 'namespace\s+([\w.]+)\s*;').Groups[1].Value
$usings = [regex]::Matches($clean, 'using\s+(TaoKeeper[\w.]*)\s*;') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

$fail = @()

# A) using 命名空间必须在依赖程序集或壳自身清单中存在
foreach ($ns in $usings) {
  if ($ns -eq $ownNs -or $appNs.Contains($ns)) { continue }
  $hit = $nsToProject.Keys | Where-Object { $_ -eq $ns -or $_ -like "$ns.*" -or $ns -like "$_.*" }
  if (-not $hit) { $fail += "A: using $ns 在依赖程序集与壳自身清单中均不存在（缺引用或拼错）" }
}

# B) 命名空间归属依赖工程时必须在 App.csproj 的 ProjectReference 中
$csprojText = Get-Content $appCsproj -Raw
$refs = [regex]::Matches($csprojText, 'ProjectReference\s+Include="([^"]+)"') | ForEach-Object { Split-Path -Leaf $_.Groups[1].Value }
foreach ($ns in $usings) {
  if ($ns -eq $ownNs -or $appNs.Contains($ns)) { continue }
  $root = ($ns -split '\.')[0..1] -join '.'
  $owner = $null
  foreach ($k in $nsToProject.Keys) { if (($k -split '\.')[0..1] -join '.' -eq $root) { $owner = $nsToProject[$k]; break } }
  if ($owner) {
    $need = Split-Path -Leaf $projects[$owner]
    if ($refs -notcontains $need) { $fail += "B: using $ns 属于 $need，App.csproj 缺 ProjectReference（CS0234 类）" }
  }
}

# C) 大写标识符唯一匹配导出类型（依赖或壳自身）-> 精确命名空间必须可解析
$idents = [regex]::Matches($clean, '(?<![.\w])([A-Z][A-Za-z0-9_]*)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($id in $idents) {
  $nss = [System.Collections.Generic.List[string]]::new()
  if ($typeMap.ContainsKey($id)) { $nss.AddRange($typeMap[$id]) }
  if ($appTypes.ContainsKey($id)) {
    $ans = $appTypes[$id]
    if (-not $nss.Contains($ans)) { $nss.Add($ans) }
  }
  if ($nss.Count -eq 0) { continue }        # BCL/WinUI/未知名，不判
  if ($nss.Count -gt 1) { continue }        # 跨源同名，无法判界，跳过
  $ns = $nss[0]
  $covered = ($usings -contains $ns) -or ($ns -eq $ownNs) -or ($clean -match [regex]::Escape("$ns.$id"))
  if (-not $covered) { $fail += "C: 类型 $id 位于 $ns，但未 using、非同命名空间且未全限定（CS0246/CS0103 类）" }
}

if ($fail.Count -gt 0) {
  Write-Host '[CHECK-CR] FAIL:'
  $fail | ForEach-Object { Write-Host "  - $_" }
  exit 1
}
Write-Host '[CHECK-CR] PASS: 组合根 using/工程引用/类型命名空间三方一致（含壳自身类型）'
