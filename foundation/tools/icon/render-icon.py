"""Render icon.png (128x128, transparent) from the shapes in icon.svg.
Run from the repo root: python foundation/tools/icon/render-icon.py"""
from PIL import Image, ImageDraw

SIZE, SS = 128, 8            # output size, supersample factor
S = SIZE * SS / 32           # svg units -> supersampled pixels

def pts(seq):
    return [(x * S, y * S) for x, y in seq]

img = Image.new("RGBA", (SIZE * SS, SIZE * SS), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.polygon(pts([(8, 0), (24, 0), (32, 16), (24, 32), (8, 32), (0, 16)]), fill="#1b2330")
# stroke-width 2.5 centred on r=7: outer radius 8.25, inner radius 5.75
cx, cy = 16 * S, 14 * S
d.ellipse([cx - 8.25 * S, cy - 8.25 * S, cx + 8.25 * S, cy + 8.25 * S], fill="#f5f4ef")
d.ellipse([cx - 5.75 * S, cy - 5.75 * S, cx + 5.75 * S, cy + 5.75 * S], fill="#1b2330")
d.polygon(pts([(9, 22), (13, 26), (9, 30), (5, 26)]), fill="#3b7bd6")
img.resize((SIZE, SIZE), Image.Resampling.LANCZOS).save("icon.png", optimize=True)
print("wrote icon.png 128x128")
