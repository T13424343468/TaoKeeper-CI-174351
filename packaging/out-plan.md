# out/ 交付目录规划（TaoKeeper PC-FULL-V1 / MSI 1.0.0.0）

> 口径依据：`tao-build-pitfalls` 第九章（安装器风格）第 1、4 条 + 第八章第 29 条（哈希三元组）。
> 本文件只落**结构与清单口径**；out/ 实体由真机 build-msi.bat + 操作卡组装，沙箱不落实体。

## 一、out/ 最终结构

```
out/
├── TaoKeeper-PC-MSI-v1.0.0.msi      ← 唯一 MSI（build-msi.bat 产出，文件名全 ASCII）
├── hashes.txt                        ← SHA-256 清单（make-hashes 脚本产出，覆盖下列全部文件）
├── package-manifest.md               ← 交付清单（packaging/ 拷入，包内每文件来源与用途）
├── docs/                             ← 随包文档（6 份，从 docs/ 原样拷入）
│   ├── MSI打包说明.md
│   ├── Windows真机首验清单.md
│   ├── 交付说明.md
│   ├── 功能与边界.md
│   ├── 快速上手.md
│   └── 操作说明.md
└── TaoKeeper-src-v1.0.0/             ← 源码快照（排除构建产物，见第三节）
    ├── README.md
    ├── README.txt
    ├── TaoKeeper.sln
    ├── build.bat
    ├── build.ps1
    ├── src/          （5 个工程，源文件原样）
    ├── tests/        （3 个测试工程 + nolib-probe 探针）
    ├── native/TaoKeeperScan/   （C++ 源，不含 build/ 输出）
    └── tools/msi/    （Product.wxs、build-msi.bat、precheck-sandbox.sh，不含 publish/out/.precheck）
```

## 二、逐项规则（组装时核对）

| 项 | 规则 | 出处 |
|---|---|---|
| MSI 数量 | out 内 `*.msi` **必须恰好 1 个**，文件名 `TaoKeeper-PC-MSI-v1.0.0.msi` | 第九章 1；build-msi.bat 结尾自检 |
| MSI 文件名 | 全 ASCII，零中文、零空格 | 第九章 4 |
| 免安装绿色版 | publish 目录**不拷出**、不进 out，与 MSI 互不干扰 | 第九章 1 |
| hashes.txt | 位于 out 顶层，覆盖 out 下全部文件（含 msi），规范见 `hashes-spec.md` | 第八章 29 |
| package-manifest.md | 从 `packaging/` 原样拷入 out 顶层，不改内容 | 第八章 29（顶层清单） |
| docs/ | 6 份随包文档原样拷入；源码快照内**不再重复** docs/（重建不需要文档，避免双份漂移） | 本线拍板 |
| 源码快照目录名 | `TaoKeeper-src-v1.0.0`，全 ASCII | 第九章 4 |

## 三、源码快照排除清单（拷贝时排除，缺一不可）

排除的是**构建产物与临时态**，防止陈旧二进制假绿与空壳包（第八章 29、第九章 31 口径）：

- `src/**/bin/`、`src/**/obj/`（全部 5 个工程的编译中间物）
- `native/TaoKeeperScan/build/`（CMake 产物含旧绝对路径，快照带走必然污染）
- `tools/msi/publish/`（发布载荷，真机自产）
- `tools/msi/out/`（构建输出）
- `tools/msi/.precheck/`（沙箱预检临时副本）
- `dist/`、`out/` 自身（构建报告与包输出）
- 任何 `*.log`、`*.user`、`.vs/`（如有）

**保留**：`packaging/` 不进快照（构建期工具，见第一节）；`docs/` 不进快照（已在 out/docs/）。

## 四、禁止事项

1. out/ 内不得出现第二个 msi、zip、7z（第九章 1）。
2. 不得把 `native/TaoKeeperScan/build/`、`bin/`、`obj/` 拷进快照（陈旧产物假绿，第八章 9）。
3. 不得改 msi 文件名（版本一致性核对锚点，第九章 5）。
4. 交付通知必带校验三元组：msi 字节数 + SHA-256 + out 顶层清单（第八章 29）。
