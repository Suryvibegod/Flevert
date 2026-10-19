"""Draws assets/demo/convert.gif: four phone photos selected in Explorer, right-click, Flevert > Convert > JPG, and the
JPG copies appearing beside them with Flevert's notification. An illustration in the style of Windows 11, drawn
from scratch: the photos are generated gradients, and the menu labels and notification text are the app's own.

Usage (Windows, for the Segoe UI font): python make_demo_gif.py convert.gif
"""
import math
import os
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

S = 2  # drawn at twice the size, then reduced, for smooth edges
W, H = 820, 500
FONT = "C:/Windows/Fonts/segoeui.ttf"
FONT_SEMIBOLD = "C:/Windows/Fonts/seguisb.ttf"
ICON = Image.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "icon-256.png")).convert("RGBA")

ACCENT = (0, 103, 192)
SELECT_FILL = (204, 228, 247)
SELECT_LINE = (153, 201, 239)
TEXT = (26, 26, 26)
MUTED = (96, 96, 96)


def font(size, bold=False):
    return ImageFont.truetype(FONT_SEMIBOLD if bold else FONT, size * S)


def scaled(box):
    return [v * S for v in box]


def photo(seed, size):
    """A small synthetic landscape: sky gradient, sun, hills and water. No real photo content."""
    w, h = size
    skies = [((255, 166, 102), (255, 222, 173)), ((92, 156, 230), (190, 225, 250)),
             ((116, 86, 170), (250, 160, 140)), ((60, 140, 160), (200, 235, 225))]
    top, bottom = skies[seed % 4]
    img = Image.new("RGB", (w, h))
    px = ImageDraw.Draw(img)
    for y in range(h):
        t = y / h
        px.line([(0, y), (w, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    sun_x = [0.7, 0.25, 0.5, 0.8][seed % 4] * w
    px.ellipse([sun_x - h * 0.12, h * 0.22, sun_x + h * 0.12, h * 0.46], fill=(255, 245, 210))
    hill = [(0, h * 0.62)] + [(x, h * (0.58 + 0.06 * math.sin(x / w * 6 + seed))) for x in range(0, w + 1, 4)] + [(w, h), (0, h)]
    px.polygon(hill, fill=[(70, 110, 80), (50, 95, 120), (90, 70, 110), (40, 110, 100)][seed % 4])
    px.rectangle([0, h * 0.8, w, h], fill=[(60, 120, 170), (40, 90, 150), (80, 80, 150), (30, 120, 140)][seed % 4])
    for i in range(5):
        y = h * (0.83 + i * 0.035)
        px.line([(w * 0.1 + i * 9, y), (w * 0.4 + i * 9, y)], fill=(255, 255, 255, 90), width=max(1, S))
    return img


NAMES = ["IMG_2041", "IMG_2042", "IMG_2043", "IMG_2044"]
TILE = (148, 112)  # thumbnail size
GRID_X, GRID_Y = 52, 150
STEP_X, STEP_Y = 182, 160
THUMBS = [photo(i, (TILE[0] * S, TILE[1] * S)) for i in range(4)]


def tile_origin(index):
    return GRID_X + (index % 4) * STEP_X, GRID_Y + (index // 4) * STEP_Y


def rounded(draw, box, radius, **kw):
    draw.rounded_rectangle(scaled(box), radius=radius * S, **kw)


def shadow(canvas, box, radius, blur=14, offset=6, alpha=70):
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    x0, y0, x1, y1 = box
    d.rounded_rectangle(scaled([x0, y0 + offset, x1, y1 + offset]), radius=radius * S, fill=(0, 0, 0, alpha))
    canvas.alpha_composite(layer.filter(ImageFilter.GaussianBlur(blur * S)))


def draw_window(canvas, files, selected):
    shadow(canvas, (20, 18, 800, 482), 10)
    d = ImageDraw.Draw(canvas)
    rounded(d, (20, 18, 800, 482), 10, fill=(255, 255, 255), outline=(220, 220, 220), width=S)
    # Title bar and address bar.
    rounded(d, (20, 18, 800, 60), 10, fill=(243, 243, 243))
    d.rectangle(scaled([20, 50, 800, 60]), fill=(243, 243, 243))
    folder = Image.new("RGBA", (18 * S, 14 * S), (0, 0, 0, 0))
    fd = ImageDraw.Draw(folder)
    fd.rounded_rectangle([0, 2 * S, 18 * S, 14 * S], radius=2 * S, fill=(255, 200, 60))
    fd.rectangle([0, 0, 8 * S, 4 * S], fill=(240, 180, 40))
    canvas.alpha_composite(folder, (36 * S, 31 * S))
    d.text((62 * S, 29 * S), "Holiday photos", font=font(13), fill=TEXT)
    # Minimise, maximise, close.
    d.line(scaled([706, 39, 716, 39]), fill=MUTED, width=S)
    d.rectangle(scaled([740, 34, 749, 43]), outline=MUTED, width=S)
    d.line(scaled([774, 34, 783, 43]), fill=MUTED, width=S)
    d.line(scaled([774, 43, 783, 34]), fill=MUTED, width=S)
    d.line(scaled([20, 60, 800, 60]), fill=(229, 229, 229), width=S)
    rounded(d, (110, 72, 640, 102), 6, fill=(255, 255, 255), outline=(214, 214, 214), width=S)
    d.text((126 * S, 78 * S), "Pictures   \u203a   Holiday photos", font=font(12), fill=TEXT)
    for i, glyph in enumerate(["\u2190", "\u2192", "\u2191"]):
        d.text(((36 + i * 24) * S, 76 * S), glyph, font=font(14), fill=MUTED)
    rounded(d, (652, 72, 784, 102), 6, fill=(255, 255, 255), outline=(214, 214, 214), width=S)
    d.text((666 * S, 78 * S), "Search", font=font(12), fill=(140, 140, 140))
    d.line(scaled([20, 114, 800, 114]), fill=(235, 235, 235), width=S)
    d.text((40 * S, 124 * S), f"{len(files)} items" + (f"    {len(selected)} items selected" if selected else ""), font=font(11), fill=MUTED)

    for index, (name, thumb) in enumerate(files):
        x, y = tile_origin(index)
        if index in selected:
            rounded(d, (x - 8, y - 8, x + TILE[0] + 8, y + TILE[1] + 34), 6, fill=SELECT_FILL, outline=SELECT_LINE, width=S)
        canvas.paste(thumb, (x * S, y * S))
        d = ImageDraw.Draw(canvas)
        label = font(12)
        width = d.textlength(name, font=label) / S
        d.text(((x + TILE[0] / 2 - width / 2) * S, (y + TILE[1] + 8) * S), name, font=label, fill=TEXT)


MENU_ITEMS = ["Open", "Edit", "Print", "Rotate right", None, "Flevert", None, "Share", "Copy as path", "Properties"]
SUB_ITEMS = ["Compress", "Convert", "Remove metadata"]
FORMATS = ["JPG", "PNG", "WEBP", "AVIF", "GIF", "BMP", "TIFF"]
ROW = 30


def menu_size(items, width):
    height = 8 + sum(9 if item is None else ROW for item in items)
    return width, height


def draw_menu(canvas, origin, items, width, highlight=None, arrows=(), icons=()):
    x, y = origin
    w, h = menu_size(items, width)
    shadow(canvas, (x, y, x + w, y + h), 8, blur=10, offset=4, alpha=60)
    d = ImageDraw.Draw(canvas)
    rounded(d, (x, y, x + w, y + h), 8, fill=(249, 249, 249), outline=(212, 212, 212), width=S)
    cy = y + 4
    rows = {}
    for item in items:
        if item is None:
            d.line(scaled([x + 10, cy + 4, x + w - 10, cy + 4]), fill=(222, 222, 222), width=S)
            cy += 9
            continue
        if item == highlight:
            rounded(d, (x + 4, cy + 1, x + w - 4, cy + ROW - 1), 4, fill=(232, 232, 232))
        if item in icons:
            icon = ICON.resize((16 * S, 16 * S), Image.LANCZOS)
            canvas.alpha_composite(icon, ((x + 14) * S, (cy + 7) * S))
            d = ImageDraw.Draw(canvas)
        d.text(((x + 40) * S, (cy + 6) * S), item, font=font(12), fill=TEXT)
        if item in arrows:
            d.text(((x + w - 22) * S, (cy + 5) * S), "\u203a", font=font(13), fill=MUTED)
        rows[item] = (cy, cy + ROW)
        cy += ROW
    return rows


def draw_cursor(canvas, pos, pressed=False):
    x, y = pos
    arrow = [(0, 0), (0, 17), (4.5, 13), (7.5, 20), (10, 19), (7, 12.5), (12.5, 12.5)]
    d = ImageDraw.Draw(canvas)
    d.polygon([((x + px) * S, (y + py) * S) for px, py in arrow], fill=(255, 255, 255) if not pressed else (225, 225, 225), outline=(0, 0, 0), width=S)


def draw_toast(canvas, progress):
    if progress <= 0:
        return
    w, h = 300, 84
    x = W - 14 - w + int((1 - progress) * 60)
    y = H - 14 - h
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    shadow(layer, (x, y, x + w, y + h), 8, blur=10, offset=4, alpha=int(80 * progress))
    d = ImageDraw.Draw(layer)
    rounded(d, (x, y, x + w, y + h), 8, fill=(238, 244, 250, int(255 * progress)), outline=(205, 214, 224, int(255 * progress)), width=S)
    icon = ICON.resize((20 * S, 20 * S), Image.LANCZOS)
    icon.putalpha(icon.getchannel("A").point(lambda a: int(a * progress)))
    layer.alpha_composite(icon, ((x + 16) * S, (y + 14) * S))
    d.text(((x + 44) * S, (y + 14) * S), "Flevert", font=font(12), fill=(*MUTED, int(255 * progress)))
    d.text(((x + 16) * S, (y + 44) * S), "Done: 4 files", font=font(14, bold=True), fill=(*TEXT, int(255 * progress)))
    canvas.alpha_composite(layer)


def background():
    bg = Image.new("RGBA", (W * S, H * S))
    d = ImageDraw.Draw(bg)
    for y in range(H * S):
        t = y / (H * S)
        d.line([(0, y), (W * S, y)], fill=(int(214 + 20 * t), int(228 + 12 * t), int(246 - 6 * t), 255))
    return bg


def lerp(a, b, t):
    return a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t


def ease(t):
    return t * t * (3 - 2 * t)


def frames():
    originals = [(f"{name}.HEIC", thumb) for name, thumb in zip(NAMES, THUMBS)]
    jpgs = [(f"{name}.jpg", thumb) for name, thumb in zip(NAMES, THUMBS)]
    rest = (660, 430)

    def scene(files, selected=(), cursor=rest, pressed=False, menus=(), toast=0.0, band=None):
        canvas = background()
        draw_window(canvas, files, set(selected))
        if band:
            layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
            ImageDraw.Draw(layer).rectangle(scaled(band), fill=(0, 103, 192, 40), outline=(0, 103, 192, 160), width=S)
            canvas.alpha_composite(layer)
        for menu in menus:
            draw_menu(canvas, *menu)
        draw_toast(canvas, toast)
        draw_cursor(canvas, cursor, pressed)
        return canvas.convert("RGB").resize((W, H), Image.LANCZOS)

    yield scene(originals), 900
    # Drag a selection rectangle over the four photos.
    start, end = (34, 136), (770, 300)
    for i in range(1, 9):
        t = ease(i / 8)
        corner = lerp(start, end, t)
        covered = [k for k in range(4) if tile_origin(k)[0] < corner[0] - 20]
        yield scene(originals, covered, corner, band=(start[0], start[1], corner[0], corner[1])), 60
    selected = range(4)
    for i in range(1, 7):
        yield scene(originals, selected, lerp(end, (300, 150), ease(i / 6))), 50
    yield scene(originals, selected, (300, 150), pressed=True), 150

    main = ((300, 150), MENU_ITEMS, 220)
    rows = draw_menu(Image.new("RGBA", (W * S, H * S)), *main)
    snap_top = rows["Flevert"][0]
    main_args = dict(arrows=("Flevert",), icons=("Flevert",))
    yield scene(originals, selected, (306, 156), menus=[(*main, None, *main_args.values())]), 450
    for i in range(1, 6):
        yield scene(originals, selected, lerp((306, 156), (380, snap_top + 15), ease(i / 5)), menus=[(*main, None, *main_args.values())]), 45
    sub = ((516, snap_top - 4), SUB_ITEMS, 190)
    sub_rows = draw_menu(Image.new("RGBA", (W * S, H * S)), *sub)
    with_main = (*main, "Flevert", *main_args.values())
    yield scene(originals, selected, (380, snap_top + 15), menus=[with_main, (*sub, None, ("Compress", "Convert"), ())]), 400
    convert_mid = sub_rows["Convert"][0] + 15
    for i in range(1, 6):
        yield scene(originals, selected, lerp((380, snap_top + 15), (580, convert_mid), ease(i / 5)),
                    menus=[with_main, (*sub, "Compress" if i < 3 else "Convert", ("Compress", "Convert"), ())]), 45
    # Opens upwards where there is no room below, as Windows does.
    formats_height = menu_size(FORMATS, 112)[1]
    formats = ((702, min(sub_rows["Convert"][0] - 4, 474 - formats_height)), FORMATS, 112)
    format_rows = draw_menu(Image.new("RGBA", (W * S, H * S)), *formats)
    with_sub = (*sub, "Convert", ("Compress", "Convert"), ())
    yield scene(originals, selected, (580, convert_mid), menus=[with_main, with_sub, (*formats, None, (), ())]), 350
    jpg_mid = format_rows["JPG"][0] + 15
    for i in range(1, 5):
        yield scene(originals, selected, lerp((580, convert_mid), (730, jpg_mid), ease(i / 4)),
                    menus=[with_main, with_sub, (*formats, "JPG" if i > 2 else None, (), ())]), 50
    yield scene(originals, selected, (730, jpg_mid), menus=[with_main, with_sub, (*formats, "JPG", (), ())]), 600
    yield scene(originals, selected, (730, jpg_mid), pressed=True, menus=[with_main, with_sub, (*formats, "JPG", (), ())]), 120

    # The JPG copies appear beside the originals, then the notification.
    for n in range(1, 5):
        files = sorted(originals + jpgs[:n], key=lambda f: (f[0][:8], f[0][9:] != "HEIC"))
        yield scene(files, selected=[i for i, f in enumerate(files) if f[0].endswith("HEIC")], cursor=(730, jpg_mid)), 140
    files = sorted(originals + jpgs, key=lambda f: (f[0][:8], f[0][9:] != "HEIC"))
    heic = [i for i, f in enumerate(files) if f[0].endswith("HEIC")]
    for i in range(1, 7):
        yield scene(files, heic, (730, jpg_mid), toast=ease(i / 6)), 50
    yield scene(files, heic, (730, jpg_mid), toast=1.0), 2600


def main(out):
    images, durations = zip(*frames())
    images[0].save(out, save_all=True, append_images=list(images[1:]), duration=list(durations), loop=0, optimize=True, disposal=1)


if __name__ == "__main__":
    main(sys.argv[1])
