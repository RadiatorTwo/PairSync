# Draws the Windows program icon src/PairSync.Desktop/Assets/PairSync.ico from the PairSync mark
# (same as build/linux/pairsync.svg and AppIcon.cs). Needs Pillow; run again only when the mark changes.
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 1024
UNIT = SIZE / 32

image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
# SVG strokes are centered on the edge, Pillow draws them inside the box: widen the box by half the stroke.
stroke = 2.5 * UNIT
draw.rectangle([3 * UNIT - stroke / 2, 3 * UNIT - stroke / 2, 20 * UNIT + stroke / 2 - 1, 20 * UNIT + stroke / 2 - 1],
               outline=(0x1F, 0x30, 0x43, 255), width=round(stroke))
draw.rectangle([12 * UNIT, 12 * UNIT, 30 * UNIT - 1, 30 * UNIT - 1], fill=(0x59, 0x80, 0xA6, 255))

target = Path(__file__).resolve().parent.parent / "src" / "PairSync.Desktop" / "Assets" / "PairSync.ico"
image.save(target, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print(target)
