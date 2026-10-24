import sys
from PIL import Image, ImageDraw, ImageFont
import pillow_heif
pillow_heif.register_heif_opener()
out = sys.argv[1]
# Must match HeicText in ImageToTextTests.cs.
lines = [
    "Hóa đơn bán hàng số 12345",
    "Cộng hòa xã hội chủ nghĩa Việt Nam",
    "Độc lập - Tự do - Hạnh phúc",
]
w, h = 1600, 420
img = Image.new("RGB", (w, h), (255, 255, 255))
d = ImageDraw.Draw(img)
font = ImageFont.truetype("arial.ttf", 64)  # Windows Arial, as the tests that draw text at run time use
for i, line in enumerate(lines):
    d.text((60, 50 + i * 110), line, fill=(0, 0, 0), font=font)
img.save(out, format="HEIF", quality=80)
print(pillow_heif.__version__, pillow_heif.libheif_info())
