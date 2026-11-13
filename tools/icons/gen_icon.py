#!/usr/bin/env python3
"""生成青瓷玄盾多尺寸 ico（R-1 图标资产包）。

色板对齐原型 v19 天青主题：纸 #F0F7FB / 瓷青 #4E8296 / 线 #7FA8BD / 墨 #2B2A26。
画法：1024 超采样绘制盾形 + 渐变 + 内衬 + 扫帚意象，降采样出 7 档尺寸打包 ico。
"""
from PIL import Image, ImageDraw
import os

S = 1024  # 超采样底板
PAPER = (240, 247, 251, 0)      # 透明底（贴深浅桌面都不糊）
TEAL_TOP = (111, 163, 184)      # 6FA3B8 瓷青亮
TEAL_DEEP = (78, 130, 150)      # 4E8296 瓷青深
LINE = (127, 168, 189)          # 7FA8BD
INK = (43, 42, 38)              # 2B2A26
SWEEP = (240, 247, 251, 255)    # 纸白扫痕


def shield_points(cx, cy, w, h):
    """盾形参数点：上缘微弧、两肩内收、下尖圆润。t∈[0,1) 沿周界。"""
    pts = []
    # 右半：顶中 → 右肩 → 右腰 → 下尖；左半镜像
    # 用分段二次贝塞尔近似，采样密集保证平滑
    def qbez(p0, p1, p2, n=24):
        return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t ** 2 * p2[0],
                 (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t ** 2 * p2[1])
                for t in [i / n for i in range(n + 1)]]

    x0, y0 = cx, cy - h / 2          # 顶中
    xr, yr = cx + w / 2, cy - h * 0.28  # 右肩外
    xw, yw = cx + w * 0.46, cy + h * 0.12  # 右腰
    xb, yb = cx, cy + h / 2          # 下尖

    right = qbez((x0, y0), (cx + w * 0.34, cy - h * 0.44), (xr, yr))
    right += qbez((xr, yr), (cx + w * 0.52, cy - h * 0.10), (xw, yw))[1:]
    right += qbez((xw, yw), (cx + w * 0.40, cy + h * 0.34), (xb, yb))[1:]
    left = [(2 * cx - x, y) for (x, y) in reversed(right[1:-1])]
    return right + left


def build_base():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy = S / 2, S * 0.52
    outline = shield_points(cx, cy, S * 0.80, S * 0.86)

    # 外描边（墨色描线，深浅底都立得住）
    d.polygon(outline, fill=INK + (255,))
    inner = shield_points(cx, cy, S * 0.72, S * 0.78)
    # 瓷青垂直渐变（上亮下深）
    grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    top, bottom = int(cy - S * 0.43), int(cy + S * 0.43)
    for y in range(max(0, top), min(S, bottom)):
        t = (y - top) / max(1, bottom - top)
        r = int(TEAL_TOP[0] + (TEAL_DEEP[0] - TEAL_TOP[0]) * t)
        g = int(TEAL_TOP[1] + (TEAL_DEEP[1] - TEAL_TOP[1]) * t)
        b = int(TEAL_TOP[2] + (TEAL_DEEP[2] - TEAL_TOP[2]) * t)
        gd.line([(0, y), (S, y)], fill=(r, g, b, 255))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).polygon(inner, fill=255)
    img.paste(grad, (0, 0), mask)

    # 内衬高光（上缘一线纸白，青瓷釉面感）
    hi = shield_points(cx, cy - S * 0.012, S * 0.66, S * 0.72)
    d.line(hi + [hi[0]], fill=(240, 247, 251, 150), width=int(S * 0.014), joint="curve")

    # 对勾意象（清理 = 一扫而净、妥当）：纸白粗线，两段直线圆头衔接，小尺寸不糊
    w = int(S * 0.075)
    d.line([(S * 0.36, S * 0.55), (S * 0.465, S * 0.665)], fill=SWEEP, width=w)
    d.line([(S * 0.465, S * 0.665), (S * 0.68, S * 0.40)], fill=SWEEP, width=w)
    r = w // 2
    for (px, py) in [(S * 0.36, S * 0.55), (S * 0.465, S * 0.665), (S * 0.68, S * 0.40)]:
        d.ellipse([px - r, py - r, px + r, py + r], fill=SWEEP)
    return img


def main():
    out_dir = os.path.join(os.path.dirname(__file__), "..", "..", "assets", "icons")
    out_dir = os.path.abspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    base = build_base()
    sizes = [16, 20, 24, 32, 48, 64, 256]
    frames = [base.resize((s, s), Image.LANCZOS) for s in sizes]
    ico_path = os.path.join(out_dir, "taokepper-shield.ico")
    frames[-1].save(ico_path, format="ICO",
                    sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    # 256 档单出 PNG（README/文档配图同源）
    frames[-1].save(os.path.join(out_dir, "taokepper-shield-256.png"), format="PNG")
    print("written:", ico_path)


if __name__ == "__main__":
    main()
