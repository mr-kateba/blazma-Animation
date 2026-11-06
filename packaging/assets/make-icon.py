#!/usr/bin/env python3
"""Regenerates blazma.png (256) and blazma.ico (16..256) with Pillow. Run only when the icon design changes."""
import os
from PIL import Image, ImageDraw, ImageFont
here = os.path.dirname(os.path.abspath(__file__))
font_path = os.path.join(here, "../../app/godot/assets/fonts/IBMPlexSansArabic-SemiBold.ttf")
S = 1024
im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
d.rounded_rectangle((40, 40, S - 40, S - 40), radius=220, fill=(24, 28, 38, 255))
d.rounded_rectangle((40, 40, S - 40, S - 40), radius=220, outline=(255, 140, 50, 255), width=36)
f = ImageFont.truetype(font_path, 640)
d.text((S / 2, S / 2 + 20), "B", font=f, fill=(255, 140, 50, 255), anchor="mm")
# a small bone-chain motif: three joints joined by lines
pts = [(250, 800), (420, 860), (600, 800)]
d.line(pts, fill=(235, 238, 245, 255), width=26)
for x, y in pts:
    d.ellipse((x - 34, y - 34, x + 34, y + 34), fill=(235, 238, 245, 255))
im.resize((256, 256), Image.LANCZOS).save(os.path.join(here, "blazma.png"))
im.save(os.path.join(here, "blazma.ico"), sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
