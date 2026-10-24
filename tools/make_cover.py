"""Build the 512x512 Steam Workshop cover for the LLM_API mod.

Composition, chosen around the whale's actual silhouette (measured, not guessed):

  * the source art is 480x480 with the whale filling almost all of it, so there is no empty
    band anywhere on the left or right;
  * the one genuinely free space is the TOP CORNERS: the topmost rows only carry the whale's
    spout/head between x=211 and x=304, leaving both upper corners empty;
  * so "Let LLM Play Game" goes top-LEFT and the Chinese line goes top-RIGHT, and neither
    crosses the artwork. The whale itself sits below that band.

Run with the bundled python (needs Pillow).
"""

from PIL import Image, ImageDraw, ImageFont
import math
import os

SRC = r"F:\WorkSpace\deepseek.webp"
OUT = r"F:\WorkSpace\cs_gameapi\workshop_preview_512.png"    # repo copy, 512 for GitHub/Steam page
BIG = r"F:\WorkSpace\cs_gameapi\workshop_preview_1024.png"   # high-res master
COVER = r"F:\WorkSpace\cs_gameapi\cover.png"                 # 100x100, exactly what the game wants

SIZE = 512

FONT_EN = r"C:\Windows\Fonts\ariblk.ttf"       # Arial Black - compact and heavy
FONT_EN2 = r"C:\Windows\Fonts\arialbd.ttf"
FONT_CN = r"C:\Windows\Fonts\msyhbd.ttc"       # Microsoft YaHei Bold


def background(size):
    """Deep navy, lifted slightly towards the centre so the whale is not floating on a flat fill."""
    bg = Image.new("RGB", (size, size), (16, 22, 48))
    px = bg.load()
    cx = cy = size / 2.0
    maxd = math.hypot(cx, cy)
    for y in range(size):
        for x in range(size):
            d = math.hypot(x - cx, y - cy) / maxd          # 0 centre .. 1 corner
            # radial lift: brighter near the middle
            lift = max(0.0, 1.0 - d) ** 1.6
            # vignette: darken the outer edge
            vig = 1.0 - 0.45 * (max(0.0, d - 0.45) / 0.55) ** 1.4
            r = int((16 + 30 * lift) * vig)
            g = int((24 + 38 * lift) * vig)
            b = int((56 + 74 * lift) * vig)
            px[x, y] = (r, g, b)
    return bg


def draw_text_with_glow(canvas, xy, text, font, fill, anchor="la", glow=6):
    """White text on a navy field reads better with a soft dark halo behind it."""
    x, y = xy
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for radius in range(glow, 0, -2):
        for dx in (-radius, 0, radius):
            for dy in (-radius, 0, radius):
                if dx == 0 and dy == 0:
                    continue
                d.text((x + dx, y + dy), text, font=font, fill=(0, 0, 0, 90), anchor=anchor)
    canvas.alpha_composite(layer)
    ImageDraw.Draw(canvas).text((x, y), text, font=font, fill=fill, anchor=anchor)


def main():
    canvas = background(SIZE).convert("RGBA")

    # --- the artwork, sized and placed so the text band stays clear of it --------------
    # The source fills its own frame almost edge to edge with a character whose head reaches
    # the very top, so the text cannot simply be laid over a corner. The art is scaled down
    # and pushed below a reserved band instead: TEXT_BAND pixels of clean background, then
    # the picture. That keeps "Let LLM Play Game" off the character entirely.
    TEXT_BAND = 92
    whale = Image.open(SRC).convert("RGBA")
    bbox = whale.getbbox()
    if bbox:
        whale = whale.crop(bbox)

    avail_h = SIZE - TEXT_BAND - 8          # 8px of breathing room at the bottom
    scale = min(470 / whale.width, avail_h / whale.height)
    whale = whale.resize((max(1, int(whale.width * scale)), max(1, int(whale.height * scale))), Image.LANCZOS)

    wx = (SIZE - whale.width) // 2
    wy = TEXT_BAND + max(0, (avail_h - whale.height) // 2)
    canvas.alpha_composite(whale, (wx, wy))

    # --- text, entirely inside the reserved band --------------------------------------
    # Two STACKED lines, not two left/right blocks: at this width the English and Chinese
    # lines collided in the middle when they shared a row.
    en_font = ImageFont.truetype(FONT_EN, 28)
    cn_font = ImageFont.truetype(FONT_CN, 21)

    d = ImageDraw.Draw(canvas)
    en = "Let LLM Play Game"
    cn = "让大模型玩密教模拟器"
    en_w = d.textlength(en, font=en_font)
    cn_w = d.textlength(cn, font=cn_font)

    en_y, en_h = 13, 32
    cn_y, cn_h = 50, 26
    draw_text_with_glow(canvas, ((SIZE - en_w) / 2, en_y), en, en_font, (255, 255, 255, 255), "la", 5)
    draw_text_with_glow(canvas, ((SIZE - cn_w) / 2, cn_y), cn, cn_font, (188, 210, 255, 255), "la", 4)

    # A rule under the band makes it read as a deliberate header rather than stray text.
    d.line([(16, TEXT_BAND - 10), (SIZE - 16, TEXT_BAND - 10)], fill=(120, 150, 215, 130), width=1)
    print(f"english width={en_w:.0f}px (y {en_y}..{en_y + en_h})")
    print(f"chinese width={cn_w:.0f}px (y {cn_y}..{cn_y + cn_h})  gap={cn_y - (en_y + en_h)}px")
    print(f"both fit inside {SIZE}px: {en_w < SIZE - 32 and cn_w < SIZE - 32}")

    # --- strapline along the bottom ---------------------------------------------------
    strip = Image.new("RGBA", (SIZE, 42), (8, 12, 30, 210))
    canvas.alpha_composite(strip, (0, SIZE - 42))
    d = ImageDraw.Draw(canvas)
    tag_font = ImageFont.truetype(FONT_EN2, 18)
    line = "Local HTTP API   http://127.0.0.1:8710   v1.0.0"
    d.text((SIZE // 2, SIZE - 22), line, font=tag_font, fill=(198, 214, 246, 255), anchor="mm")

    # Hairline border to frame it on Steam's dark background.
    d.rectangle([0, 0, SIZE - 1, SIZE - 1], outline=(96, 120, 190, 160), width=2)

    out = canvas.convert("RGB")
    out.save(OUT, "PNG")
    out.resize((1024, 1024), Image.LANCZOS).save(BIG, "PNG")

    # The game insists on exactly 100x100 at <modfolder>/cover.png. Downscaling from this
    # composition keeps the layout; the text is unreadable at 100px by nature, which is normal
    # for a Workshop thumbnail.
    out.resize((100, 100), Image.LANCZOS).save(COVER, "PNG")

    print(f"wrote {OUT}  {out.size}")
    print(f"wrote {BIG}  (1024, 1024)")
    print(f"wrote {COVER}  (100, 100)  <- this is the one the game uploads")
    print(f"text band = 0..{TEXT_BAND}   art = y {wy}..{wy + whale.height}, x {wx}..{wx + whale.width}")
    gap = wy - TEXT_BAND
    print(f"clear gap between text band and art = {gap}px  (negative would mean overlap)")


if __name__ == "__main__":
    main()
