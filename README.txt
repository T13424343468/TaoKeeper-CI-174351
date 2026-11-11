TaoKeeper 电脑管家（PC 完整包 v1）
======================================================

【本包内容（PC-FULL-V1，2026-11-09）】
1. 包结构与全系 PC 项目统一：根目录 .sln + build.bat 一键构建 + docs/ + src/ + tests/，
   与 TaoPad / TaoZip / TaoDBM / TaoDown 同一打法规矩。
2. 构建流程真机全绿背书：Core 105/105、AppKernel 80/80、Adapters 26/26 全过，
   自检台构建通过，哈希清单与报告正常生成（真机 2026-11-09 实测）。
3. 全部自检动作只读、零写动作：不安装、不升级、不删除、不改系统设置。

【一键构建（Windows 10/11，需 .NET 8 SDK）】
双击根目录 build.bat，等它跑完即可（窗口不会闪退）。
- 没装 CMake 也能跑：C++ 核心会自动跳过，不影响其余全部环节。
- 想跳过 C++ 核心可在 cmd 里运行：build.bat -SkipNative
- 跑完在 dist\ 目录看 构建报告.txt 与 哈希清单-SHA256.txt，全绿即通过。

【构建通过后：真机自检台（只读）】
在解压目录运行：
  dotnet run --project src/TaoKeeper.Harness -- all
或分通道：-- list-software / -- list-drivers / -- driver-updates

【回传】
无论全绿还是报红，把 dist\<时间戳>\构建报告.txt 全文 + 自检台完整输出发给大光。
报红时不要自行修改系统设置或驱动，原样回传即可。
