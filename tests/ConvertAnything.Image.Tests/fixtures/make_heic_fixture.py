import sys
from PIL import Image, ImageDraw
import pillow_heif
pillow_heif.register_heif_opener()
out = sys.argv[1]
w, h = 320, 240
img = Image.new("RGB", (w, h))
px = img.load()
for y in range(h):
    for x in range(w):
        px[x, y] = (x * 255 // w, y * 255 // h, 128)
d = ImageDraw.Draw(img)
d.rectangle([20, 20, 120, 80], fill=(255, 255, 255))  # white block at top-left marks orientation
img.save(out, format="HEIF", quality=80)
print(pillow_heif.__version__, pillow_heif.libheif_info())
