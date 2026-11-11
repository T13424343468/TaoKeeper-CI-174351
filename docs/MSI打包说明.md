# TaoKeeper MSI 打包说明（微信风格单页安装器）

> 归属：TaoKeeper 冲刺 MSI 打包线（分身 F），2026-11-12。
> 规格基准：`tao-build-pitfalls` 第九章「安装器风格（微信风格 MSI）」逐条对照；
> 风格对齐 TaoPad(198806) / TaoZip(195792) / TaoDBM(196708) 统一口径。

## 一、交付物

| 文件 | 说明 |
|---|---|
| `tools/msi/Product.wxs` | 微信风格 MSI 主源（UTF-8 BOM，Codepage 936）。自绘单页 MainDlg UI 源即文件内 `<UI Id="TaoKeeperInstallUI">` 块：欢迎/许可 → 选目录 → 安装，三步即完。**UI 块勿擅动，改版走 ReCode。** |
| `tools/msi/build-msi.bat` | 真机一键构建脚本（纯 ASCII + CRLF，头带版本戳）。发布 → `wix build` → out 单 msi 自检。 |
| `tools/msi/precheck-sandbox.sh` | Linux 沙箱静态预检脚本（编译/链接阶段；本线已跑，见第六节）。 |

## 二、真机构建步骤（Windows，全程离线）

1. 前置：Windows 10 1809+；`.NET 8 SDK`；`dotnet tool install -g wix`；`wix extension add -g WixToolset.UI.wixext`（版本须与 wix 对齐，5.0.2 对 5.0.2）。
2. 出发布载荷（离线，不下载任何运行时）：
   ```powershell
   dotnet publish src\TaoKeeper.App -c Release -r win-x64 --self-contained false -o tools\msi\publish
   ```
   要求目录内有 `TaoKeeper.App.exe`（csproj 未设 AssemblyName，默认产物即此名）。
3. 双击或在终端运行 `tools\msi\build-msi.bat`（可传参指定发布目录：`build-msi.bat <publish 目录>`）。
4. 脚本自检：out 目录 msi 数必须 = 1（否则报错退出）。
5. 产物：`tools\msi\out\TaoKeeper-PC-MSI-v1.0.0.msi`（包文件名全 ASCII）。

## 三、规格对照（tao-build-pitfalls 第九章逐条）

| 条款 | 实现 | 状态 |
|---|---|---|
| 1. 只交付 MSI，out 仅 1 个 msi | build-msi.bat 结尾项数自检（≠1 即 ERROR） | ✅ 脚本已内置 |
| 2. 微信风格自绘单页 MainDlg | Product.wxs 内 `<UI>` 块，欢迎/许可→选目录→安装三步，微软雅黑、与 TaoZip/TaoDBM 同构 | ✅ 静态预检过 |
| 3a. 默认装 `D:\Program Files\TaoKeeper`（界面可改） | `<Property Id="INSTALLFOLDER" Value="D:\Program Files\TaoKeeper" />` 目录同名属性覆盖写法（TaoZip 真机反复验证；TaoDBM 的 Type-51 自定义动作写法真机触发 2723，弃用） | ✅ |
| 3b. 桌面 + 开始菜单快捷方式自动创建 | DeskShortcutF / MenuShortcutF 两特性，界面勾选（默认勾上） | ✅ |
| 3c. 「应用和功能」卸载干净；覆盖安装 MajorUpgrade 先卸后装 | MajorUpgrade（AllowSameVersionUpgrades）；快捷方式/目录组件均带 HKCU 键值 Keypath + RemoveFolder（卸载即清）；无 HKLM 注册表残留项 | ✅ |
| 4. 包文件名/标识符零中文，界面文案与注释保留中文 | 包名 `TaoKeeper`、msi 文件名、快捷方式名全 ASCII；UI 文案中文 + Codepage 936 | ✅ 字节级核对 |
| 5. 版本号六处同步，标题栏显示版本号 | 本线交付侧（wxs / 新 bat / 本说明）统一 **1.0.0.0**（映射源码既有戳 PC-FULL-V1）；源码侧现状见第四节审计 | ⚠️ 源码侧待集成 |
| 6. 打包工具内置包中，全程离线 | build-msi.bat 只调本地 dotnet/wix，MediaTemplate EmbedCab=yes（cab 内嵌），不下载任何运行时 | ✅ |

## 四、版本号六处核对（以源码内既有版本戳为准）

源码内实际版本戳（本轮逐处查明）：**PC-FULL-V1 (2026-11-09)**，无任何数字版号。
MSI 数字版号映射为 **1.0.0.0**（与 TaoPad 首包 1.0.0 同序）。

| # | 位置 | 现状 | 本线动作 |
|---|---|---|---|
| 1 | `build.bat:7` | `PC-FULL-V1 (2026-11-09)` | 禁改（共享文件），值已核对一致 |
| 2 | `build.ps1:19` | `PC-FULL-V1 (2026-11-09)` | 禁改，值已核对一致 |
| 3 | `README.txt:4` | `PC-FULL-V1 (2026-11-09)` | 禁改，值已核对一致 |
| 4 | `tools/msi/Product.wxs` | 新增，`Version="1.0.0.0"`（注释注明 PC-FULL-V1 映射） | ✅ 本线写死 |
| 5 | `tools/msi/build-msi.bat` | 新增，头部版本戳 `PC-FULL-V1 / MSI 1.0.0.0 (2026-11-12)`，文件名 v1.0.0 | ✅ 本线写死 |
| 6 | csproj / 代码 AppVersion / README.md | csproj 补 `Version/FileVersion/AssemblyVersion = 1.0.0.0`；README.md 头部补版本行 | ✅ 2026-11-12 集成线补齐 |
| — | 标题栏显示版本号 | `MainWindow` 标题栏显示 `道管家 · TaoKeeper v1.0.0.0` | ✅ 2026-11-12 集成线补齐 |

> 待集成补齐（需调度核心授权，超出本分身边界）：csproj 加 `<Version>1.0.0.0</Version>`；
> 主窗标题栏带版本号；README.md 补版本行。若集成线另有拍板（如统一改 1.0.1），wxs 六处同步改 `Version` 与 msi 文件名即可（UpgradeCode 不变）。
> ——上两项缺口已于 2026-11-12 由 P2 集成线补齐：六处现均落 **1.0.0.0（PC-FULL-V1）** 同口径。

## 五、重要口径备忘

- **UpgradeCode `5C4F1E6A-2B7D-4E93-8A60-9D3C7F2B4A15` 永久固定**，任何升版不改（改了真机无法覆盖升级）。
- 快捷方式 Target 指向 `[INSTALLFOLDER]TaoKeeper.App.exe`；若后续集成线改 AssemblyName，wxs 两处 Target 同步改。
- 未内置图标（源码树无 .ico 资产）：快捷方式与卸载列表走 exe 默认图标；待集成补「青瓷玄盾」ico 后，可加 `<Icon>` + `ARPPRODUCTICON`（TaoZip 同款写法）。
- 杀进程双动作（SetKillCmd + KillRunningTaoKeeper，taskkill `/f /im TaoKeeper.App.exe`）在装/卸/升级前结束运行中的实例，Return=ignore 不影响未运行场景。

## 六、沙箱静态预检结果（WiX 5.0.2 + UI 扩展 5.0.2，2026-11-12）

**做了**（`tools/msi/precheck-sandbox.sh`，副本打补丁后真跑）：
- wxs 结构/语法：编译 + 链接阶段通过，**零错误零新增警告**（首轮撞出 UI 扩展 wixlib 同名标准对话框冲突 WIX0091/WIX7009，自绘对话框全部改用 `TaoKeeper` 前缀唯一 ID 后消除——此经验已回灌）。
- GUID：UpgradeCode 全新生成；其余组件走 WiX v4 稳定自动 GUID（同标识不变）。
- Component 规则：快捷方式/清理组件均带 KeyPath（HKCU RegistryValue `KeyPath="yes"`），无空组件。
- 收割语法：`<Files Include="$(PublishDir)\**">` 用正斜杠副本验证可正常收割占位载荷（WIX8600 消除）。
- ASCII/BOM：wxs UTF-8 BOM 已核；build-msi.bat 0 非 ASCII 字节 + CRLF 字节级核对。
- 版本号六处：见第四节（wxs/新 bat/文档一致；源码侧缺口如实列出）。

**没做 / 做不了（沙箱物理限制）**：
- **bind 阶段依赖 Windows 原生 msi.dll，沙箱出不了 MSI**——预检以 `WIX0001 DllNotFoundException: msi.dll` 收场（EXIT=36，预期内），**MSI 最终构建一律真机 build-msi.bat，待真机构建**。
- `Directory/@Name` 在 Linux 报 WIX0389（WiX 源码硬编码 `C:\`），预检副本换 `Name="."` 绕过；交付文件保持原名。
- 安装/卸载/覆盖升级的实际体验（界面点击、快捷方式落地、卸载残留）**只能真机验证**，真机首验清单另见 `docs/Windows真机首验清单.md`。

## 七、新坑回灌记录（供调度核心回灌 tao-build-pitfalls 第九章）

- **坑**：WiX v4+ 引用 UI 扩展后，其 wixlib 自带 CancelDlg/ExitDialog/ProgressDlg/VerifyReadyDlg 等标准对话框符号；自绘 UI 若沿用这些传统 ID 会撞 WIX0091/WIX7009。
  **标准写法**：自绘对话框统一用 `<产品名>Dlg` 前缀唯一 ID（如 `TaoKeeperMainDlg`），引用处同步改。
  **出处**：TaoKeeper MSI 线 2026-11-12 沙箱预检实测（TaoZip/TaoDBM 源用传统 ID，其真机构建环境可能与本口径不同，引用其源时注意此差异）。
