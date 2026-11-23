# -*- coding: utf-8 -*-
"""把 docs/images 下的界面截图合成为 README 演示 GIF。

用法（PowerShell）：
    python scripts\make-demo-gif.py

设计取舍：
- 只用现有截图，不依赖录屏，任何机器都能重现；
- 每张图缓慢推近（Ken Burns），片间淡入淡出，弱化"纯轮播"感；
- 体积优先：宽度 720、8 fps、128 色，目标是 README 首屏能接受的 3-6 MB。
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
IMG_DIR = ROOT / "docs" / "images"
OUT = IMG_DIR / "demo.gif"

WIDTH = 720          # 画布宽度（README 显示宽度，再大对文字清晰度提升有限）
FPS = 8              # 帧率：轮播不需要高帧率，降下来直接省体积
HOLD = 2.0           # 每张停留秒数
FADE = 0.25          # 转场秒数
COLORS = 128         # 调色板颜色数，128 色对 UI 截图足够，比 256 色小很多
PAD = 24
IMG_H = 430
CAPTION_H = 38
HEIGHT = PAD + IMG_H + 14 + CAPTION_H + PAD

BG = (7, 11, 20)
CARD = (13, 20, 36)
BORDER = (58, 86, 140)
TEXT = (215, 228, 255)
ACCENT = (75, 224, 160)

SLIDES = [
    ("main-window.png", "主界面 · 一键启用"),
    ("main-window-detail.png", "待机模式 · PS5 式熄屏在线"),
    ("settings.png", "设置 · 语言 / 主题 / 服务参数"),
    ("streaming-env.png", "串流环境 · 离线依赖自检"),
]


def load_font(size: int) -> ImageFont.FreeTypeFont:
    """优先微软雅黑，其次黑体；都找不到就直接报错，避免静默输出豆腐块。"""
    for candidate in (r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\simhei.ttf"):
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, size)
    raise SystemExit("找不到可用的中文字体（msyh.ttc / simhei.ttf）")


FONT = load_font(24)


def fit(img: Image.Image, box_w: int, box_h: int) -> Image.Image:
    scale = min(box_w / img.width, box_h / img.height)
    return img.resize((round(img.width * scale), round(img.height * scale)), Image.LANCZOS)


def compose(path: Path, caption: str, zoom: float) -> Image.Image:
    """生成一帧：深色卡片 + 截图 + 底部说明。zoom>1 时从中心裁切再放大，形成缓慢推近。"""
    canvas = Image.new("RGB", (WIDTH, HEIGHT), BG)
    draw = ImageDraw.Draw(canvas)

    draw.rounded_rectangle(
        (PAD, PAD, WIDTH - PAD, PAD + IMG_H),
        radius=12,
        fill=CARD,
        outline=BORDER,
        width=1,
    )

    src = Image.open(path).convert("RGB")
    fitted = fit(src, WIDTH - 2 * PAD - 28, IMG_H - 28)
    if zoom > 1.0:
        crop_w = int(fitted.width / zoom)
        crop_h = int(fitted.height / zoom)
        left = (fitted.width - crop_w) // 2
        top = (fitted.height - crop_h) // 2
        fitted = fitted.crop((left, top, left + crop_w, top + crop_h)).resize(
            (fitted.width, fitted.height), Image.LANCZOS
        )

    x = PAD + (WIDTH - 2 * PAD - fitted.width) // 2
    y = PAD + (IMG_H - fitted.height) // 2
    canvas.paste(fitted, (x, y))

    cap_y = PAD + IMG_H + 14
    dot_r = 4
    draw.ellipse(
        (PAD + 2, cap_y + CAPTION_H // 2 - dot_r, PAD + 2 + dot_r * 2, cap_y + CAPTION_H // 2 + dot_r),
        fill=ACCENT,
    )
    draw.text((PAD + 18, cap_y + 4), caption, font=FONT, fill=TEXT)
    return canvas


def build_frames() -> list:
    hold_frames = max(2, int(HOLD * FPS))
    fade_frames = max(1, int(FADE * FPS))

    sequences = []
    for name, caption in SLIDES:
        path = IMG_DIR / name
        if not path.exists():
            raise SystemExit(f"缺少截图：{path}")
        frames = []
        for i in range(hold_frames):
            t = i / (hold_frames - 1)
            frames.append(compose(path, caption, 1.0 + 0.05 * t))
        sequences.append(frames)

    frames = []
    for index, sequence in enumerate(sequences):
        frames.extend(sequence)
        if index < len(sequences) - 1:
            start, end = sequence[-1], sequences[index + 1][0]
            for step in range(1, fade_frames + 1):
                frames.append(Image.blend(start, end, step / (fade_frames + 1)))
    return frames


def main() -> None:
    frames = [frame.convert("P", palette=Image.ADAPTIVE, colors=COLORS) for frame in build_frames()]
    frames[0].save(
        OUT,
        save_all=True,
        append_images=frames[1:],
        duration=round(1000 / FPS),
        loop=0,
        optimize=True,
        disposal=2,
    )
    size_mb = OUT.stat().st_size / 1024 / 1024
    print(f"已生成 {OUT}（{len(frames)} 帧，{size_mb:.1f} MB）")
    if size_mb > 6:
        print("提示：体积偏大，可调小 WIDTH / FPS / HOLD 后重跑。")


if __name__ == "__main__":
    main()
