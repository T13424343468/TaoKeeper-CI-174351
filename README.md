# TaoKeeper — Windows 全能电脑管家

**版本：1.0.0.0**（源码戳 PC-FULL-V1；与 `tools/msi/Product.wxs` 的 MSI 交付版号同口径）。

TaoKeeper 是 Tao 系列的全能电脑管家（对标微软电脑管家），
WinUI 3 + .NET 8 界面、C++ 扫描核心，界面采用道系水墨美学三主题
（**水墨 / 天青 / 墨白**），道生三象，只此三套；全系 Tao 系列产品统一按此三套实现。

## 模块总览（本包范围）

- **磁盘清理**：空间分析（$MFT 扫描、目录聚合 Top-N、路径重建）、垃圾清理（规则引擎+回收站语义）、重复文件（SHA-256 比对）
- **真机自检台（Harness）**：只读体检三通道——已装软件清单（winget）、第三方驱动清册（pnputil）、驱动更新候选（Windows 更新接口）；零写动作
- **已落地核心**：体检引擎、清理账本（回收站语义记账）、内存监控、弹窗规则（合规形态）、存储感知、桌面挂件、一键加速、软件管家/驱动适配层、大文件与空文件夹规划
- **WinUI 壳**：界面按原型 v19 开发中，本包暂不含图形界面

## 构建与运行

需要 Windows 10 1809+ 与 .NET 8 SDK（更高版本脚本自动滚动前向，建议装 8 完全对齐）。
**一键构建**：双击根目录 `build.bat`（或 `build.bat -SkipNative` 跳过 C++ 核心）。
构建产物（构建报告 + 哈希清单）输出到 `dist\<时间戳>\`。

构建通过后运行真机自检台（只读）：

```powershell
dotnet run --project src/TaoKeeper.Harness -- all
```

环境前置、自检台通道说明与回传约定见 `docs/操作说明.md`。

## 目录结构（全系 PC 项目统一打法规矩）

- `TaoKeeper.sln`：解决方案（Visual Studio 2022 直接打开）
- `build.bat` / `build.ps1`：一键构建入口与脚本本体
- `src/`：Core（纯 .NET 业务核心）、AppKernel（导航/主题/设置）、Adapters（winget 与驱动适配层）、Harness（真机自检台）、App（WinUI 壳，本包不含界面）
- `tests/`：三个工程的单元测试
- `native/`：C++ 扫描核心（$MFT 解析/遍历/路径/聚合）与 C ABI 导出层，CMake 构建
- `docs/`：设计文档与操作说明
