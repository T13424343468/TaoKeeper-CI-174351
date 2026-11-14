# XamlCompiler MSB3073 fast bisect v3 (backdesk, 2026-11-14)
# 修复 v2 两处缺陷：
# 1) V1 还原失败：git checkout origin/main -- <新增文件> 对 main 中不存在的文件报 pathspec 错误。
#    修法：先删工作树中的新增文件（git checkout 只对 main 已有文件生效），再 checkout；删除仅限批内文件。
# 2) V3 构建仅 7 秒：还原后未清 obj 导致增量假象；本版每组先删 obj/bin（Clean-Obj）。
# 判定：MSB3073 无 XLS 行视为编译器吞错型红；组绿=该组清白。顺序：V1 挂件+接线 → V2 挂件文件单留 → V3 接线单留。
$ErrorActionPreference = 'Continue'
$proj = 'src/TaoKeeper.App/TaoKeeper.App.csproj'

# 批内全部改动文件（相对 origin/main），新增文件在 main 不存在，须先删再测
$batchFiles = git diff --name-only origin/main HEAD | Where-Object { $_ -notmatch '^ci/|^.github/' }

function Clean-Obj {
  Remove-Item -Recurse -Force src/TaoKeeper.App/obj -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force src/TaoKeeper.App/bin -ErrorAction SilentlyContinue
}
function Restore-Batch {
  # 新增文件先删（main 无此文件，checkout 不会覆盖它），已有文件 checkout 覆盖
  foreach ($f in $batchFiles) {
    $inMain = git cat-file -e "origin/main:$f" 2>$null; if ($LASTEXITCODE -ne 0) { Remove-Item -Force $f -ErrorAction SilentlyContinue }
  }
  git checkout origin/main -- @($batchFiles) 2>&1 | Out-Null
  foreach ($f in $batchFiles) {
    $inMain = git cat-file -e "origin/main:$f" 2>$null
    if ($LASTEXITCODE -eq 0) { git checkout origin/main -- $f 2>&1 | Out-Null }
  }
}
function Restore-Keep([string[]]$keep) {
  Restore-Batch
  foreach ($f in $keep) {
    if (Test-Path $f) { git checkout HEAD -- $f 2>&1 | Out-Null }
    else { git checkout HEAD -- $(Split-Path $f -Parent) 2>&1 | Out-Null }  # 新增文件：从 HEAD 恢复目录内文件
    if (-not (Test-Path $f)) { Write-Host "[BISECT][WARN] 缺文件 $f" }
  }
}
function Build-Once([string]$label) {
  Clean-Obj
  $env:MSBUILDDISABLENODEREUSE = '1'
  dotnet build $proj -c Release -p:Platform=x64 -p:NodeReuse=false 2>&1 | Tee-Object -FilePath "bisect-$label.log" | Out-Null
  $exit = $LASTEXITCODE
  $xls = (Select-String -Path "bisect-$label.log" -Pattern 'XLS\d+' | Measure-Object).Count
  $result = if ($exit -eq 0) { 'GREEN' } else { 'RED' }
  Write-Host "[BISECT] $label = $result (exit=$exit, XLS lines=$xls)"
  if ($exit -ne 0) { Select-String -Path "bisect-$label.log" -Pattern ': error ' | Select-Object -First 4 | ForEach-Object { Write-Host ("  >> " + $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length))) } }
  return $result
}

$G_widget = @('src/TaoKeeper.App/Views/DesktopWidgetWindow.xaml','src/TaoKeeper.App/Views/DesktopWidgetWindow.xaml.cs')
$G_wire   = @('src/TaoKeeper.App/App.xaml.cs','src/TaoKeeper.App/CompositionRoot.cs','src/TaoKeeper.App/MainWindow.xaml.cs','src/TaoKeeper.App/PageRegistry.cs','src/TaoKeeper.AppKernel/Navigation/AppRoute.cs','tests/TaoKeeper.AppKernel.Tests/AppKernelTests.cs','src/TaoKeeper.App/Views/WidgetPage.xaml.cs','src/TaoKeeper.App/Views/PlaceholderPage.xaml.cs')

$r1 = Build-Once 'V1-widget-plus-wire-removed'; if ($r1 -eq 'GREEN') { Restore-Keep ($G_widget + $G_wire); Write-Host '[BISECT] 结论：真凶=G1挂件∪G4接线（其余组清白）'; exit 0 }
Restore-Keep $G_widget;            $r2 = Build-Once 'V2-widget-only-kept';   if ($r2 -eq 'RED')   { Write-Host '[BISECT] 结论：真凶=G1挂件窗文件'; exit 0 }
Restore-Keep $G_wire;              $r3 = Build-Once 'V3-wire-only-kept';     if ($r3 -eq 'RED')   { Write-Host '[BISECT] 结论：真凶=G4接线文件'; exit 0 }
Write-Host '[BISECT] 结论：G1/G4 单独皆绿，红=组合效应，进入逐文件交叉二分'
Restore-Batch
Clean-Obj
exit 0
