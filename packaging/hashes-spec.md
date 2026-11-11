# hashes.txt 规范（SHA-256 交付清单）

> 依据：`tao-build-pitfalls` 第八章 29 条（交付包通知必带校验三元组：字节数 + SHA-256 + 顶层清单；接收端先验哈希再解包）。

## 一、生成与校验

| 动作 | Linux / 沙箱 | Windows |
|---|---|---|
| 生成 | `bash make-hashes.sh`（在 out/ 目录下执行） | 在 out/ 目录运行 `make-hashes.bat` |
| 校验（接收端） | `sha256sum -c hashes.txt`（在 out/ 目录下执行） | `Get-Content hashes.txt` 逐行核对，或重跑 `make-hashes.bat` 后 `git diff --no-index` / `fc.exe` 比对；**内容任何差异 = 包被改或损坏** |

两脚本算法与输出格式完全一致；同一 out/ 在两端生成结果应逐字节相同（换行符除外，见下）。

## 二、格式规范

1. **行格式**：`<十六进制小写 sha256>␠␠<相对路径>`——哈希与路径之间**恰好两个空格**（sha256sum / Get-FileHash 通用口径，可直接 `sha256sum -c`）。
2. **路径口径**：
   - 相对 out/ 根（脚本所在目录），不用 `./` 前缀、不用绝对路径；
   - 分隔符一律 `/`（正斜杠），Windows 生成的也转成正斜杠；
   - 覆盖 out/ 下全部文件，**递归**，排序后输出（保证可 diff）。
3. **排除**：`hashes.txt` 自身不列入（否则自引用）。
4. **编码**：文件内容 ASCII（路径若含非 ASCII 文件名=包组装错误，应先在操作卡红灯拦截，见 out-plan 命名铁律）。
5. **换行符**：
   - `make-hashes.sh` 输出 LF；
   - `make-hashes.bat` 输出 CRLF（Windows 记事本/for 循环友好）。
   校验以哈希行为准，`sha256sum -c` 对 CRLF 结尾行会报错——Windows 生成的清单若要在 Linux 校验，先 `sed 's/\r$//'`（此差异为已接受口径，不影响哈希值本身）。
6. **msi 行必在**：`TaoKeeper-PC-MSI-v1.0.0.msi` 的哈希行是三元组之一，汇报模板直接引用。

## 三、脚本行为约定（两版一致）

- 工作目录 = 脚本所在目录（`cd "$(dirname "$0")"` / `cd /d "%~dp0"`），即 out/ 根；
- 幂等：重复执行覆盖旧 hashes.txt，不追加；
- 失败即停：任一文件哈希失败（读权限/文件消失）非零退出，不留半份清单；
- 生成完打印文件计数，供操作卡核对（计数 = out/ 文件总数 − 1，不含 hashes.txt 自身）。
