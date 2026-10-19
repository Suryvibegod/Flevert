"""Writes blended-240x180.webp: a 6-frame lossless animated WebP as gif2webp or WebPAnimEncoder with RGBA input make it.

Each frame is quantised on its own (as GIF frames are) while a shape moves a few pixels, so scattered pixels change all
over the canvas and most stay the same. The encoder then stores full-canvas frames with alpha blending, where transparent
pixels mean "keep the previous frame"; written without first expanding the frames, most of each frame becomes
transparent. (Larger steps or plain gradients make the encoder store ordinary opaque frames instead.)
"""
import random
import sys
from PIL import Image, ImageChops, ImageDraw

out = sys.argv[1]
w, h = 240, 180
random.seed(1)
coarse = Image.frombytes("RGB", (w // 24 + 2, h // 24 + 2), bytes(random.getrandbits(8) for _ in range((w // 24 + 2) * (h // 24 + 2) * 3)))
fine = Image.frombytes("RGB", (w // 3, h // 3), bytes(random.getrandbits(8) for _ in range((w // 3) * (h // 3) * 3)))
background = Image.blend(coarse.resize((w, h), Image.BICUBIC), fine.resize((w, h), Image.BICUBIC), 0.15)
noise = Image.merge("RGB", [Image.effect_noise((w, h), 6) for _ in range(3)])
background = ImageChops.add(background, noise, scale=1.0, offset=-128)

frames = []
for i in range(6):
    frame = background.copy()
    ImageDraw.Draw(frame).ellipse([i * 3, i * 2, i * 3 + 60, i * 2 + 60], fill=(255, 0, 0))
    frames.append(frame.quantize(64).convert("RGBA"))

frames[0].save(out, save_all=True, append_images=frames[1:], duration=80, loop=0, lossless=True)
print("ok")
