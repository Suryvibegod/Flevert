"""Draws the ConvertAnything icon (two arrows chasing each other on a rounded tile) and writes app.ico and a PNG preview.

Usage: python assets/make_icon.py   (needs Pillow). Drawn at 1024 px and downscaled per size, so it stays reproducible
and owned by the repository.
"""
import math
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
SIZE = 1024
TOP, BOTTOM = (79, 70, 229), (6, 182, 212)  # indigo to cyan


def tile():
    gradient = Image.new("RGB", (SIZE, SIZE))
    pixels = gradient.load()
    for y in range(SIZE):
        for x in range(SIZE):
            t = (x + y) / (2 * (SIZE - 1))
            pixels[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM))
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle([40, 40, SIZE - 40, SIZE - 40], radius=220, fill=255)
    icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    icon.paste(gradient, (0, 0), mask)
    return icon


def arrow(draw, start, end, radius=290, width=110):
    """A clockwise arc from start to end degrees (0 = 3 o'clock, y down) with a head at end."""
    c = SIZE / 2
    box = [c - radius, c - radius, c + radius, c + radius]
    draw.arc(box, start, end, fill="white", width=width)
    t = math.radians(end)
    px, py = c + radius * math.cos(t), c + radius * math.sin(t)  # centre line of the arc at its end
    nx, ny = math.cos(t), math.sin(t)  # radial
    dx, dy = -math.sin(t), math.cos(t)  # clockwise tangent
    half, length = width * 1.25, width * 1.6
    draw.polygon([
        (px + nx * half, py + ny * half),
        (px - nx * half, py - ny * half),
        (px + dx * length, py + dy * length),
    ], fill="white")


icon = tile()
draw = ImageDraw.Draw(icon)
arrow(draw, 200, 330)
arrow(draw, 20, 150)

icon.resize((256, 256), Image.LANCZOS).save(os.path.join(HERE, "icon-256.png"))
icon.save(os.path.join(HERE, "app.ico"), sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print("ok")
