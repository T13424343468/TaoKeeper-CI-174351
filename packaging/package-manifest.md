# TaoKeeper 交付包源码清单（package-manifest）

> **包版本口径：PC-FULL-V1 / 1.0.0.0（2026-11-12）**
> 适用对象：out/ 交付包（结构见 `packaging/out-plan.md`）。
> 本清单逐项列明包内每个文件/目录的**来源**与**用途**；交付时本文件原样拷入 out/ 顶层。

## 〇、版本号六处核对表（2026-11-12 冲刺集成后口径）

| # | 位置 | 应有值 | 状态 |
|---|---|---|---|
| 1 | `build.bat` 头部版本戳 | PC-FULL-V1 (2026-11-09) | ✅ 核对一致 |
| 2 | `build.ps1` 版本戳 | PC-FULL-V1 (2026-11-09) | ✅ 核对一致 |
| 3 | `README.txt` 版本戳 | PC-FULL-V1 (2026-11-09) | ✅ 核对一致 |
| 4 | `tools/msi/Product.wxs` `Version` | 1.0.0.0（注释注明 PC-FULL-V1 映射） | ✅ |
| 5 | `tools/msi/build-msi.bat` 头部戳 / msi 文件名 | PC-FULL-V1 / MSI 1.0.0.0 / v1.0.0 | ✅ |
| 6 | `src/TaoKeeper.App/TaoKeeper.App.csproj`（Version/FileVersion/AssemblyVersion）+ `README.md` 版本行 + 标题栏 | 1.0.0.0 | ✅（本轮实测抽查 csproj 三处 + README.md 行均在） |

> 出包自检时按操作卡重跑此表；任一处不一致 = 红灯，停止出包。

## 一、来源标注说明

- **v1 基线**：2026-11-09 PC-FULL-V1 源码树既有文件。
- **A/B2/C/D/F/G 线**：2026-11-12 冲刺各分身线新增或修改（详见 `/root/workspace/projects/575558/分身交接/` 各线档案与调度账本）。
- **归属存疑**：个别文件基线/冲刺归属无法从交接档案确定时，如实标注「逐文件归属见 git 历史」，不编造。

## 二、out/ 顶层

| 文件/目录 | 来源 | 用途 |
|---|---|---|
| `TaoKeeper-PC-MSI-v1.0.0.msi` | 真机 build-msi.bat 产出（F 线脚本 + Product.wxs） | 唯一交付安装器：微信风格单页安装、默认装 `D:\Program Files\TaoKeeper`、桌面+开始菜单快捷方式、「应用和功能」卸载干净、覆盖安装先卸后装。全程离线，cab 内嵌 |
| `hashes.txt` | 真机出包时由 make-hashes 脚本生成 | SHA-256 清单，覆盖 out 下全部文件，接收端先验哈希再解包 |
| `package-manifest.md` | K 线（本文件） | 交付清单：包内文件来源与用途 |
| `docs/` | G 线 + F 线 + 基线 | 随包文档 6 份，见第三节 |
| `TaoKeeper-src-v1.0.0/` | v1 基线 + 冲刺 A/B2/C/D/F/G 线 | 完整源码快照（排除构建产物），可在另一台机器重建；含 MSI 构建链 |

## 三、out/docs/（随包文档，6 份）

| 文件 | 来源 | 用途 |
|---|---|---|
| `MSI打包说明.md` | F 线 | MSI 构建规格：真机步骤、第九章逐条对照、版本六处、UpgradeCode 固定值、沙箱预检结果与新坑记录 |
| `Windows真机首验清单.md` | 基线 | 装机后首次验证步骤（安装体验、快捷方式、卸载残留、原生崩溃诊断兜底） |
| `交付说明.md` | G 线 | 本包交付范围与边界总述 |
| `功能与边界.md` | G 线 | 功能清单与明确不做的边界（合规口径） |
| `快速上手.md` | G 线 | 5 分钟上手：构建、运行、真机自检台 |
| `操作说明.md` | 基线 | 环境、构建与真机自检台通道说明 |

## 四、out/TaoKeeper-src-v1.0.0/ 源码快照

### 4.1 根目录

| 文件 | 来源 | 用途 |
|---|---|---|
| `README.md` | v1 基线 + D 线补版本行 | 项目总述、模块总览、构建运行说明、目录结构 |
| `README.txt` | v1 基线 | 版本戳承载文件（PC-FULL-V1，2026-11-09） |
| `TaoKeeper.sln` | v1 基线 | 解决方案（VS2022 直接打开；含全部 src/tests 工程） |
| `build.bat` | v1 基线 | 一键构建入口（Windows，头部版本戳；`-SkipNative` 跳过 C++ 核心） |
| `build.ps1` | v1 基线 | 构建脚本本体（输出构建报告 + 哈希清单到 dist\时间戳） |

### 4.2 src/TaoKeeper.Core — 纯 .NET 业务核心（v1 基线，下同）

| 文件 | 用途 |
|---|---|
| `TaoKeeper.Core.csproj` | 工程文件 |
| `Models/CleanWidgetModels.cs` | 桌面挂件清理数据模型 |
| `Models/FileEntry.cs` | 文件条目模型 |
| `Models/MftStreamScanModels.cs` | $MFT 流式扫描数据模型 |
| `Models/ScanProgress.cs` | 扫描进度模型 |
| `Models/StartupProcessModels.cs` | 启动项/进程模型 |
| `Modules/CheckupContextBuilder.cs` | 体检上下文构建 |
| `Modules/CleanWidgetPolicy.cs` | 挂件清理策略 |
| `Modules/CleanupCoordinator.cs` | 清理协调（回收站语义记账） |
| `Modules/CleanupLedger.cs` | 清理账本 |
| `Modules/CleanupSafetyGate.cs` | 清理安全门（防误删） |
| `Modules/DriverUpdatePlan.cs` | 驱动更新计划 |
| `Modules/DuplicateFileFinder.cs` | 重复文件查找（SHA-256 比对） |
| `Modules/HealthCheckEngine.cs` | 体检引擎 |
| `Modules/IDiskScanner.cs` | 磁盘扫描接口 |
| `Modules/JunkRuleEngine.cs` | 垃圾规则引擎 |
| `Modules/MemoryMonitorEngine.cs` | 内存监控引擎 |
| `Modules/MftPathBuilder.cs` | $MFT 路径重建 |
| `Modules/MftStreamingScanner.cs` | $MFT 流式扫描器 |
| `Modules/PopupRuleEngine.cs` | 弹窗规则引擎（合规形态） |
| `Modules/PortableDiskScanner.cs` | 可携式磁盘扫描实现 |
| `Modules/SoftwareManagerContracts.cs` | 软件管家契约 |
| `Modules/SpeedupCoordinator.cs` | 一键加速协调 |
| `Modules/StorageSenseEngine.cs` | 存储感知引擎 |
| `Modules/ToolboxPlanner.cs` | 工具箱规划（Windows 风格路径双分隔符解析口径） |
| `Modules/WidgetDataPath.cs` | 挂件数据路径 |
| `Native/MftIncrementalAggregatorSession.cs` | 原生增量聚合会话 |
| `Native/MftStreamSession.cs` | 原生流式扫描会话 |
| `Native/TaoKeeperScanInterop.cs` | C ABI 互操作层（手动候选路径定位 + TryLoad） |
| `Services/FileHeadSampler.cs` | 文件头采样 |
| `Services/IFileHasher.cs` | 哈希接口 |
| `Services/Sha256FileHasher.cs` | SHA-256 文件哈希实现 |

### 4.3 src/TaoKeeper.AppKernel — 导航/主题/设置/ViewModel

| 文件 | 来源 | 用途 |
|---|---|---|
| `TaoKeeper.AppKernel.csproj` | v1 基线 | 工程文件 |
| `Navigation/AppRoute.cs` | v1 基线 + D 线接线（`AppRouteCatalog` 11 路由登记） | 路由枚举与目录 |
| `Settings/AppSettings.cs` | v1 基线 | 设置模型 |
| `Settings/AppSettingsStore.cs` | v1 基线 | 设置存取 |
| `Theming/ThemeTokens.cs` | v1 基线 | 三主题（水墨/天青/墨白）令牌 |
| `ViewModels/CheckupPageViewModel.cs` | A 线 | 体检页 VM |
| `ViewModels/CleanupQueueViewModel.cs` | v1 基线（或冲刺新增，归属见 git 历史） | 清理队列 VM |
| `ViewModels/DiskCleanupPageViewModel.cs` | A 线 | 磁盘清理页 VM |
| `ViewModels/DriverPageViewModel.cs` | B 线 | 驱动页 VM |
| `ViewModels/DuplicateFilesPageViewModel.cs` | A 线 | 重复文件页 VM |
| `ViewModels/HomePageViewModel.cs` | v1 基线（D 线修 using） | 首页 VM |
| `ViewModels/MemPageViewModel.cs` | v1 基线 | 内存页 VM |
| `ViewModels/PopupPageViewModel.cs` | v1 基线 | 弹窗页 VM |
| `ViewModels/SettingsPageViewModel.cs` | C 线新写 | 设置页 VM |
| `ViewModels/SoftwarePageViewModel.cs` | v1 基线（B 线接线 winget） | 软件管家页 VM |
| `ViewModels/SpaceAnalysisPageViewModel.cs` | A 线 | 空间分析页 VM |
| `ViewModels/SpeedupPageViewModel.cs` | B2 线（D 线修进程排序） | 加速页 VM |
| `ViewModels/StartupPageViewModel.cs` | B2 线 | 启动项页 VM |
| `ViewModels/StorageSensePageViewModel.cs` | v1 基线 | 存储感知页 VM（页面无独立路由，工具箱卡片进入） |
| `ViewModels/ToolboxPageViewModel.cs` | v1 基线 | 工具箱页 VM |
| `ViewModels/WidgetPageViewModel.cs` | v1 基线 | 挂件页 VM |

### 4.4 src/TaoKeeper.Adapters — winget/驱动适配层

| 文件 | 用途 |
|---|---|
| `TaoKeeper.Adapters.csproj` | 工程文件 |
| `PnputilOutputParser.cs` | pnputil 输出解析（第三方驱动清册） |
| `WindowsDriverUpdateAdapter.cs` | Windows 更新接口适配（驱动更新候选） |
| `WingetOutputParser.cs` | winget 输出解析（已装软件清单） |
| `WingetSoftwareSourceAdapter.cs` | winget 软件源适配 |

### 4.5 src/TaoKeeper.App — WinUI 3 壳

| 文件 | 来源 | 用途 |
|---|---|---|
| `TaoKeeper.App.csproj` | v1 基线 + D 线补 1.0.0.0 版本 | WinUI 3 工程文件（含 Version/FileVersion/AssemblyVersion） |
| `App.xaml` / `App.xaml.cs` | v1 基线 | 应用入口 |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | v1 基线 + D 线接线（标题栏带 v1.0.0.0） | 主窗口 |
| `PageRegistry.cs` | v1 基线 + D 线（11 页注册 + Provider 接线） | 页面注册表 |
| `PlaceholderPage.xaml` / `.cs` | v1 基线 | 占位页 |
| `Themes/TaoTheme.Mobai.xaml` 等 3 份 | v1 基线 | 墨白/水墨/天青三主题资源 |
| `Theming/AppTheme.cs` | v1 基线 | 主题切换 |
| `Views/` 15 页 × 2 文件 | 见下表 | 各功能页 XAML + code-behind |

`Views/` 逐页归属：

| 页面 | 来源 | 说明 |
|---|---|---|
| `HomePage` | v1 基线 | 首页（12 页之 Home） |
| `CheckupPage` | A 线 | 只读体检三通道 |
| `DiskCleanupPage` | A 线 | 磁盘清理（空间分析/垃圾清理/重复文件整合页） |
| `SpaceAnalysisPage` | A 线 | 空间分析（工具箱卡片进入，无独立路由） |
| `DuplicateFilesPage` | A 线 | 重复文件（同上） |
| `BoostPage` | B2 线 | 一键加速 |
| `StartupPage` | B2 线 | 启动项（加速页内嵌段复用其 Bind） |
| `SettingsPage` | C 线 | 设置页 |
| `SoftwarePage` | v1 基线（B 线接线 winget） | 软件管家 |
| `DriverPage` | v1 基线（B 线接线） | 驱动管家 |
| `MemPage` / `PopupPage` / `StoragePage` / `ToolsPage` / `WidgetPage` | v1 基线 | 内存/弹窗/存储/工具箱/挂件 |

### 4.6 src/TaoKeeper.Harness — 真机自检台

| 文件 | 用途 |
|---|---|
| `TaoKeeper.Harness.csproj` | 工程文件 |
| `Program.cs` | 只读体检三通道入口：winget 已装软件 / pnputil 驱动清册 / Windows 更新候选；`dotnet run --project src/TaoKeeper.Harness -- all` |

### 4.7 tests/ — 单元测试（3 工程 + 探针）

| 文件 | 来源 | 用途 |
|---|---|---|
| `TaoKeeper.Core.Tests/`（csproj + 20 个测试文件） | v1 基线 | 核心引擎全量单测（体检/清理/账本/安全门/重复文件/内存/弹窗/存储感知/加速/挂件/MFT 流式与增量聚合/路径重建/工具箱/互操作等） |
| `TaoKeeper.AppKernel.Tests/`（csproj + 21 个测试文件） | v1 基线 + A/B2/C/D 线新增（`LineA*`、`LineB2_*`、`LineB_*`、`LineC_*`、`LineD_*`、`SoftwarePageWingetWiringTests`） | VM 层全量单测；冲刺各线新签用例以 `Line*` 前缀命名可辨 |
| `TaoKeeper.Adapters.Tests/`（csproj + 2 个测试文件） | v1 基线 | winget/pnputil 解析与适配层单测 |
| `nolib-probe/`（`probe.csproj` + `Program.cs`） | v1 基线 | 原生库缺失场景探针：验证 `TaoKeeperScanNative.IsAvailable` 降级路径与会话创建行为 |

### 4.8 native/TaoKeeperScan — C++ 扫描核心

| 文件 | 用途 |
|---|---|
| `CMakeLists.txt` | CMake 构建定义（静态库 + 动态库 + 5 个测试） |
| `include/tk/mft.hpp` | $MFT 解析/遍历接口 |
| `include/tk/scan.hpp` | 扫描器接口 |
| `include/tk/interop_abi.h` | C ABI 导出定义 |
| `src/mft.cpp` | $MFT 实现 |
| `src/scan.cpp` | 扫描器实现 |
| `src/interop.cpp` | C ABI 导出实现 |
| `tests/test_mft*.cpp` × 4 | MFT 解析/树遍历/流式/聚合增量测试 |
| `tests/test_scan.cpp` | 扫描器测试 |

> 快照**不含** `native/TaoKeeperScan/build/`（CMake 产物含沙箱旧绝对路径，带走必污染，真机 CMake 全量重建）。

### 4.9 tools/msi — MSI 构建链

| 文件 | 来源 | 用途 |
|---|---|---|
| `Product.wxs` | F 线（UTF-8 BOM，Codepage 936） | 微信风格 MSI 主源：自绘单页 MainDlg（勿擅动）、默认装 D:\Program Files\TaoKeeper、双快捷方式、MajorUpgrade、杀进程双动作 |
| `build-msi.bat` | F 线（纯 ASCII + CRLF，头部版本戳） | 真机构建入口：校验发布载荷 → wix build（`-pdbtype none`，cab 内嵌）→ out 单 msi 自检 |
| `precheck-sandbox.sh` | F 线 | Linux 沙箱静态预检脚本（编译/链接阶段验证） |

> 快照**不含** `tools/msi/publish/`、`tools/msi/out/`、`tools/msi/.precheck/`（真机自产或临时副本）。

## 五、本清单自验口径（K 线交活自查记录）

1. 快照应含文件数与来源树（排除 bin/obj/build 等后）一致，操作卡中有计数核对步骤。
2. 版本六处核对表逐处实测：csproj 三处 + README.md 行（本轮 grep 实测在位）、build.bat/build.ps1/README.txt 戳（v1 基线）、wxs/bat（F 线）。
3. 本文件只描述包内容，不替代 out-plan 的组装规则；两文件冲突时以 out-plan 为准并回报红灯。
