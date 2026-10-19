# Photo-like 12 MP test images (smooth shapes, gradients, fine grain), as an iPhone would produce: HEIC, JPG, PNG.
# Input for scripts/measure-convert.ps1. Usage: python make_bench_photos.py <output folder>
# Needs Pillow, pillow-heif and NumPy. The images are synthetic; nothing third-party is in them.
import sys
import numpy as np
from PIL import Image, ImageFilter
import pillow_heif

pillow_heif.register_heif_opener()
out = sys.argv[1]
w, h = 4032, 3024
rng = np.random.default_rng(1)
y, x = np.mgrid[0:h, 0:w].astype(np.float32)
base = np.stack([
    128 + 90 * np.sin(x / 300) * np.cos(y / 410),
    128 + 80 * np.cos(x / 520 + 1) * np.sin(y / 260),
    128 + 70 * np.sin((x + y) / 700),
], axis=-1)
blobs = Image.fromarray(rng.integers(0, 255, (h // 32, w // 32, 3), dtype=np.uint8)).resize((w, h), Image.BICUBIC)
img = base * 0.6 + np.asarray(blobs, dtype=np.float32) * 0.4 + rng.normal(0, 6, (h, w, 3))
photo = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).filter(ImageFilter.SHARPEN)
photo.save(f"{out}/photo.heic", format="HEIF", quality=80)
photo.save(f"{out}/photo.jpg", quality=92)
photo.save(f"{out}/photo.png")
print("ok")
