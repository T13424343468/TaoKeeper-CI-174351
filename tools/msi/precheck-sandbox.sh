#!/usr/bin/env bash
# TaoKeeper MSI 沙箱静态预检（Linux 沙箱专用，非真机构建脚本）。
# 前提：/root/workspace/.local/bin/wix（WiX 5.0.2）+ WixToolset.UI.wixext 5.0.2 已装（见 wix-msi-sandbox Skill）。
# 做什么：编译+链接阶段校验（wxs 结构、符号冲突、收割语法）。
# 不做什么：bind 阶段依赖 Windows 原生 msi.dll，沙箱物理出不了 MSI——预期以
#           WIX0001 DllNotFoundException: msi.dll 收场（EXIT=36），这不是配置错误。
# 已知口径差（tao-build-pitfalls 第六章）：
#   - Directory/@Name 在 Linux 必报 WIX0389（WiX 源码硬编码 C:\），预检副本换成 Name="."；
#   - <Files Include="$(PublishDir)\**"> 的反斜杠通配在 Linux 不匹配，预检副本换 "/" 仅为验证语法；
#     真机交付文件保持反斜杠原样（Windows 语义）。
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$HERE/.precheck"
export DOTNET_ROOT=/root/workspace/.local/dotnet
WIX=/root/workspace/.local/bin/wix

mkdir -p "$WORK/publish"
printf 'dummy-payload-for-compile-stage-check' > "$WORK/publish/TaoKeeper.App.exe"
cp "$HERE/Product.wxs" "$WORK/Product.wxs"
# 口径差补丁（只打在副本上，交付文件不动）
python3 - "$WORK/Product.wxs" <<'EOF'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
s = s.replace('<Directory Id="INSTALLFOLDER" Name="TaoKeeper" />',
              '<Directory Id="INSTALLFOLDER" Name="." />')
s = s.replace('Include="$(PublishDir)\\**"', 'Include="$(PublishDir)/**"')
open(p, 'w', encoding='utf-8').write(s)
print('[precheck] patch applied to copy (Name=. / forward-slash glob)')
EOF

cd "$WORK"
# 预期：零编译/链接错误，收场于 bind 期 msi.dll 缺失（EXIT=36）
"$WIX" build -ext WixToolset.UI.wixext -culture zh-CN -arch x64 -pdbtype none \
  -define "PublishDir=publish" -o out-check.msi Product.wxs 2>&1 \
  | grep -v "WIX0000" || true
echo "[precheck] done. PASS 判据：上方仅出现 msi.dll DllNotFoundException（bind 期），无 WIX 编译/链接错误、无 WIX8600 收割零文件告警。"
