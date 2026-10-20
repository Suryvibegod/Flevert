r"""Makes the right-click menu and PDF tools icons in assets/menu/ from Microsoft's Fluent UI System Icons (MIT).

The .ico files are committed, like assets/app.ico; nothing here ships. Each SVG is downloaded from a pinned commit of
https://github.com/microsoft/fluentui-system-icons and checked against its SHA256 before use. Filled icons are tinted
with ACCENT (readable on the light and the dark menu); Color icons keep their own colours. Every .ico holds 16, 20, 24,
32, 40 and 48 px, drawn from the Fluent size that fits best (a size that divides it, else the next larger), so the menu
is sharp at every DPI. Also writes the Fluent licence beside them (THIRD-PARTY-NOTICES, scripts/deps.json).

Usage, from the repository root, in a throw-away virtual environment outside the repository:
    python -m venv %TEMP%\flevert-icons
    %TEMP%\flevert-icons\Scripts\python -m pip install --require-hashes --only-binary=:all: -r scripts/make-menu-icons.requirements.txt
    %TEMP%\flevert-icons\Scripts\python -I scripts/make-menu-icons.py
"""
import hashlib
import io
import os
import struct
import sys
import urllib.parse
import urllib.request

import resvg_py
from PIL import Image

COMMIT = "83b26348c811f072429427ff48362238656524fe"
BASE = "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/" + COMMIT + "/"
LICENCE = ("LICENSE", "69bc45dc42b9acb96a69823adbc6ae538374e3c0bde169b855b32c48eaaef52f")
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(HERE), "assets", "menu")
SIZES = [16, 20, 24, 32, 40, 48]
# A mid blue: at least 3:1 against the light (#F9F9F9) and the dark (#2C2C2C) menu, as WCAG asks of graphics.
ACCENT = "#2F80ED"
BACKGROUNDS = ["#F9F9F9", "#2C2C2C"]
FLUENT_INK = 'fill="#212121"'

# Fluent name and variant. The file is assets/menu/<name>.ico; MenuOperations.Looks and MenuCascade use the name.
ICONS = [
    # The right-click menu (docs/specs/pdf.md, "Icon").
    ("arrow_swap", "filled"),              # Convert to
    ("arrow_minimize", "filled"),          # Compress
    ("merge", "filled"),                   # Merge PDFs
    ("split_vertical", "filled"),          # Split into pages
    ("arrow_rotate_clockwise", "filled"),  # Rotate
    ("lock_closed", "filled"),             # Protect with password (Filled, to pair with lock_open, which has no Color)
    ("lock_open", "filled"),               # Remove password
    ("shield", "color"),                   # Remove hidden info
    ("document_pdf", "filled"),            # Combine into PDF
    ("toolbox", "color"),                  # Flevert PDF tools, the second PDF menu
    # The second PDF menu, Flevert PDF tools (its own entry shows toolbox).
    ("document_dismiss", "filled"),        # Remove blank pages
    ("document_page_number", "filled"),    # Add page numbers
    ("draw_text", "filled"),               # Add watermark…
    ("layout_column_two", "filled"),       # Print layout
    ("page_fit", "filled"),                # Fit pages to A4
    ("layer", "filled"),                   # Lock filled forms
    ("cut", "filled"),                     # Split by size
    ("image_multiple", "filled"),          # Extract images
]

# Every SVG used, with its SHA256 at COMMIT.
SOURCES = {
    "assets/Arrow Swap/SVG/ic_fluent_arrow_swap_16_filled.svg": "b2d161d250e439335a5698206ec01122a13ef8afaf57a228f99b2c55858fdfb1",
    "assets/Arrow Swap/SVG/ic_fluent_arrow_swap_20_filled.svg": "4ff765bd07ecdd617de082fec62a6a7b19bf64a9cf688594efead85215f66d25",
    "assets/Arrow Swap/SVG/ic_fluent_arrow_swap_24_filled.svg": "5cf50183a3f371ea37d8f1a16139343c38351433636f21b8572b3dd951a413b2",
    "assets/Arrow Minimize/SVG/ic_fluent_arrow_minimize_16_filled.svg": "3d8f53c53a36bc870fa28ce0aa43c79895eb3d68e2544bdc6771535474e44319",
    "assets/Arrow Minimize/SVG/ic_fluent_arrow_minimize_20_filled.svg": "b16141b7e316e7dc6146446466dd4c38a76fb1056009784346d8e726c4cb0f04",
    "assets/Arrow Minimize/SVG/ic_fluent_arrow_minimize_24_filled.svg": "3cafb87a264c297611121080ad68d481ccc739761977652c3e6a6d9709459eb7",
    "assets/Merge/SVG/ic_fluent_merge_16_filled.svg": "bf2adfdcf32bbf68bbcca48287c61acc24e2c12bfbae9d5688d4c6b184a1a8ac",
    "assets/Merge/SVG/ic_fluent_merge_20_filled.svg": "1bc3de6efb08a08f043f6675833af77c2dbdd67c515c00ea79be1adf0b3eb2a7",
    "assets/Merge/SVG/ic_fluent_merge_24_filled.svg": "5e27398ffe6024af8a2da224609c4cfe722da216214189865664393bd8e88936",
    "assets/Split Vertical/SVG/ic_fluent_split_vertical_16_filled.svg": "f26c3c700c28003b91db7f59703bf7713474b6b836da64ed225e9f7114f73cab",
    "assets/Split Vertical/SVG/ic_fluent_split_vertical_20_filled.svg": "63c4427882959ea1959eb9cb14e45e77ff4a02f6c9944f1336984353df521736",
    "assets/Split Vertical/SVG/ic_fluent_split_vertical_24_filled.svg": "fd67da3d8dc45749595c477c0aa9e59a43cb823e4b52d273f8e10357c3432332",
    "assets/Split Vertical/SVG/ic_fluent_split_vertical_32_filled.svg": "99ac7db9e57c1d549c1f5a2a8993a5636f21bc518f423d26019696d1c3fbca3e",
    "assets/Split Vertical/SVG/ic_fluent_split_vertical_48_filled.svg": "708c5c36e94f519638697b04037a171d45e72895ae94c71edfacfbb1b9f88abf",
    "assets/Arrow Rotate Clockwise/SVG/ic_fluent_arrow_rotate_clockwise_16_filled.svg": "5803cf903b6d04e63ebd750ce732dc8fbd99a31757c0ab7dd3d7aa8b32463759",
    "assets/Arrow Rotate Clockwise/SVG/ic_fluent_arrow_rotate_clockwise_20_filled.svg": "789e39dc7896fd4b3e00cd487434a51eb547bdb9cad18519f83c4cf7dea2250f",
    "assets/Arrow Rotate Clockwise/SVG/ic_fluent_arrow_rotate_clockwise_24_filled.svg": "961ca9cc75b4da5faeee47b8dd83107a4e0263ac1d0b6770df5a2078db4b4e4b",
    "assets/Arrow Rotate Clockwise/SVG/ic_fluent_arrow_rotate_clockwise_32_filled.svg": "31db5498b9392c014bd350f1b4b7865fccc89c5f106f1d13cd2ff29349433906",
    "assets/Arrow Rotate Clockwise/SVG/ic_fluent_arrow_rotate_clockwise_48_filled.svg": "74df23430eff1a531749748eecb0271f670352ac36f8db5a68261887564a27d2",
    "assets/Lock Closed/SVG/ic_fluent_lock_closed_16_filled.svg": "11d4254c5a23a7eef8ca8ea8256382e7002bc4d14d18b6ac796fe2c17f8f6b69",
    "assets/Lock Closed/SVG/ic_fluent_lock_closed_20_filled.svg": "6d96a9eede6659dfc561745f78bffb26c5a8492c0f1bacef72bb7fe9a76cc8f5",
    "assets/Lock Closed/SVG/ic_fluent_lock_closed_24_filled.svg": "915658d4bf304763ee959f7d404c9e6c3726f0ef4789930bcdd17139754c5d0b",
    "assets/Lock Closed/SVG/ic_fluent_lock_closed_32_filled.svg": "9850a7b66fd6fea7cf6f4a77d6bdb9085feccb5dee428311e9e68fd96316688e",
    "assets/Lock Closed/SVG/ic_fluent_lock_closed_48_filled.svg": "651712f98fa98a2af6b41d3e2e39adb7e41674905b6fea99e4813a733806a6e6",
    "assets/Lock Open/SVG/ic_fluent_lock_open_16_filled.svg": "edfb31d0146f49b31418c2bfef6be8647af025b2975c68b4cc0b4535dbbabaf0",
    "assets/Lock Open/SVG/ic_fluent_lock_open_20_filled.svg": "bb20ea72fa6e4fb7bb20d0b7819c567fb63a675a1753f0a0c88c9541ebaef65c",
    "assets/Lock Open/SVG/ic_fluent_lock_open_24_filled.svg": "8c22a2245439e1c0b548313bd292be2a093d2d53acb5db4404dc02e2188987b4",
    "assets/Lock Open/SVG/ic_fluent_lock_open_32_filled.svg": "b4e8625a0616c1743af5d7e4ca317e655ddb26564ed773581082a972eff21a7d",
    "assets/Lock Open/SVG/ic_fluent_lock_open_48_filled.svg": "bb6138a71ee36eb1b9953d32799e559ff1ffb1a112a5fceb2016cfa1b3b48937",
    "assets/Shield/SVG/ic_fluent_shield_16_color.svg": "683146b98f86c3cec5a56566a2bcca19c8d845450082f94d804d2f386c1c825c",
    "assets/Shield/SVG/ic_fluent_shield_20_color.svg": "0568ba1104174ceda4bd4165a82a7b87fda86958d098224474e369b397753e56",
    "assets/Shield/SVG/ic_fluent_shield_24_color.svg": "07961e5eb514a6ede3640760d5af4ff1ccc07559400c1f0b397e9fdc986462c2",
    "assets/Shield/SVG/ic_fluent_shield_32_color.svg": "f36ea7fdb1640cc4dd1354a1281b5a8d962551615c5ad1e88f74ac24c32ac89d",
    "assets/Shield/SVG/ic_fluent_shield_48_color.svg": "cc47f64255d0aa6381a76af26d752b3fc68e0e94d0bf457769eb2fbbebb54238",
    "assets/Document PDF/SVG/ic_fluent_document_pdf_16_filled.svg": "c12a01d6766b58e4b1b9274e8fd80ac4e31de23924c0f7397b9a14143241eef2",
    "assets/Document PDF/SVG/ic_fluent_document_pdf_20_filled.svg": "00a349402c6df8835acd78ba882eac32ab5b8d4bc9e375da4a90e0ae329acccb",
    "assets/Document PDF/SVG/ic_fluent_document_pdf_24_filled.svg": "85d20dfe3382e76395ee6b7aa8e3f4a60ff6cc4f49a9997df852a7fe62c251e5",
    "assets/Document PDF/SVG/ic_fluent_document_pdf_32_filled.svg": "d09b1225cce9751aea1c15eb6e8eb2f4607ea1449c3da6bdf2e7772093061e5b",
    "assets/Toolbox/SVG/ic_fluent_toolbox_16_color.svg": "0c0ec7c5e00f2289f4279cefe39f8146a38f1397e5082f1d15fa4797ff5ef6c2",
    "assets/Toolbox/SVG/ic_fluent_toolbox_20_color.svg": "7fa2ff969eda4d18361df407d04fc296c8b8255f704b86f6dcc2754810301d4c",
    "assets/Toolbox/SVG/ic_fluent_toolbox_24_color.svg": "779aaaa9f41f34cdd24aa47e749001f5b79cef371cde354e91b62fad407156fa",
    "assets/Toolbox/SVG/ic_fluent_toolbox_32_color.svg": "def1aa2882dec05cba1501fb7bbb12572f238d5fdc9624eade98b86c517146b0",
    "assets/Document Dismiss/SVG/ic_fluent_document_dismiss_16_filled.svg": "799a6c81985210d709b341e57b0c7a31110df3c09e83e851db57317986905665",
    "assets/Document Dismiss/SVG/ic_fluent_document_dismiss_20_filled.svg": "1d639333daa53062f9eaa01801668985dfd89bff2fa81211633b9df208c62771",
    "assets/Document Dismiss/SVG/ic_fluent_document_dismiss_24_filled.svg": "02f5a3afd815de60ea99505ea3ee5f2a2977f5b4633c7eac757a7120f9f7e464",
    "assets/Document Dismiss/SVG/ic_fluent_document_dismiss_48_filled.svg": "edca35d09d683e4e3db412663a816dd74dd45a4d1dfeb8960ca1a418e8a699ae",
    "assets/Layout Column Two/SVG/ic_fluent_layout_column_two_16_filled.svg": "ade4f475de6f98cfd28d0b3cd7fc2985c4a308ff0a11d899452b3ed17e73db7e",
    "assets/Layout Column Two/SVG/ic_fluent_layout_column_two_20_filled.svg": "664f6866af3d1dd15c07c2966a43cbc90b1e855edbf91b5f0966dcfca8fb37dd",
    "assets/Layout Column Two/SVG/ic_fluent_layout_column_two_24_filled.svg": "225e80dad188509a0c685b2b572c8c62df56bd4a558b7bbfe0175f0baf041352",
    "assets/Layout Column Two/SVG/ic_fluent_layout_column_two_32_filled.svg": "9b446fa7596540e7768d7f43f24f05ffab400d0087eb57f0319653a468c45c4f",
    "assets/Page Fit/SVG/ic_fluent_page_fit_16_filled.svg": "1f5321bd8333f1a3a68f4a9bdc14f591aacbd25518fe842366b58995fb461be5",
    "assets/Page Fit/SVG/ic_fluent_page_fit_20_filled.svg": "4f6ceeb1876ec03a97dd2cfe7597e95a178f4fe2e7137969912e069ff0c2832e",
    "assets/Page Fit/SVG/ic_fluent_page_fit_24_filled.svg": "880fb8fab27cf7054d3ad2f00fdc44a0d43cc148f19dc2757d211bfd82fabde1",
    "assets/Cut/SVG/ic_fluent_cut_16_filled.svg": "ddf1445eca88e4773f72caff078d2785ec559c477a1c71f8a25e9149ff7a6460",
    "assets/Cut/SVG/ic_fluent_cut_20_filled.svg": "f42086cf93be33b80e74e4e0c07098d37c3bf635a63e838e01aa36a51d112d12",
    "assets/Cut/SVG/ic_fluent_cut_24_filled.svg": "0d6d056dd75f9a4531d7463324bb9f682f5f1cbb1aee7f477aa75dacd6313f29",
    "assets/Cut/SVG/ic_fluent_cut_32_filled.svg": "ba256df467c57a59ec751c053e8994566c395b2e5c1bcc92253ed19f97776765",
    "assets/Cut/SVG/ic_fluent_cut_48_filled.svg": "e07b7a9a3ce84c246bfaeaad15284b016b92045763af20a75d725460e6af502d",
    "assets/Draw Text/SVG/ic_fluent_draw_text_20_filled.svg": "ab34cc4410f3962918e3318607e6bd561a74bc0cd2a4d0c6881c9a675f724a2e",
    "assets/Draw Text/SVG/ic_fluent_draw_text_24_filled.svg": "cccf2b3c8fd95799ea777cbb46abb6c42c0f2610f3509f97007c087d7b81c5e7",
    "assets/Document Page Number/SVG/ic_fluent_document_page_number_20_filled.svg": "33af33242c1e213179531744894af8a3a1cd2a9547b2e6b33dff68c2be01c222",
    "assets/Document Page Number/SVG/ic_fluent_document_page_number_24_filled.svg": "1c50cc1a8f3cfd3e92a5b96bbcbd2331844e0e29b80867396df483bc8eb59190",
    "assets/Layer/SVG/ic_fluent_layer_20_filled.svg": "e49594a1aec88b5ccde1e75423971691c7fcaac8fbb9aede0ccbc94957db2162",
    "assets/Layer/SVG/ic_fluent_layer_24_filled.svg": "fd4ccc33686ae06945c60d238347140eff9e8a7ab06737083f7a73412fe4084f",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_16_filled.svg": "fe5ebb1e154264b9736ce4a23754d9fcd4e19b8df4a4e3369a9c45d7f1438b88",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_20_filled.svg": "102f8a7e476ed0c49dbb28ac9837546ce4a64cdb78957c963db70ca7af773291",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_24_filled.svg": "095e0446adef1be2e40b0bb79548109398e45d478fbaed2e6d07e52c6cd8d717",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_32_filled.svg": "16d2103eef3fff8f99543d1b460e6aff7643c21dd012139345ad87da3bacbe86",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_48_filled.svg": "45ed2703baff95e741edfb73d9f39d16863a5061ae7fb1212248241610290ccb",
}


def luminance(colour):
    def channel(c):
        c = int(colour[c:c + 2], 16) / 255
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5)


def contrast(a, b):
    la, lb = sorted([luminance(a), luminance(b)], reverse=True)
    return (la + 0.05) / (lb + 0.05)


def fetch(path, sha256):
    with urllib.request.urlopen(BASE + urllib.parse.quote(path), timeout=60) as response:
        data = response.read()
    actual = hashlib.sha256(data).hexdigest()
    if actual != sha256:
        sys.exit(f"{path}: SHA256 {actual}, expected {sha256}")
    return data


def source_for(name, variant, size):
    """The Fluent size drawn for a target size: the largest that divides it, else the next larger, else the largest."""
    available = {}
    for path in SOURCES:
        stem = os.path.basename(path)[len("ic_fluent_"):-len(".svg")]
        base, _, rest = stem.rpartition("_" + variant)
        if rest == "" and base.rsplit("_", 1)[0] == name:
            available[int(base.rsplit("_", 1)[1])] = path
    if not available:
        sys.exit(f"No pinned SVG for {name} ({variant})")
    dividing = [s for s in available if s <= size and size % s == 0]
    larger = [s for s in available if s >= size]
    return available[max(dividing) if dividing else min(larger) if larger else max(available)]


def render(svg, variant, size):
    text = svg.decode("utf-8")
    if variant == "filled":
        if FLUENT_INK not in text:
            sys.exit("A Filled icon without Fluent's ink colour; the tint would not apply.")
        text = text.replace(FLUENT_INK, f'fill="{ACCENT}"')
    png = bytes(resvg_py.svg_to_bytes(svg_string=text, width=size, height=size))
    image = Image.open(io.BytesIO(png)).convert("RGBA")
    if image.size != (size, size):
        sys.exit(f"Rendered {image.size}, wanted {size} px")
    return image


def dib(image):
    """An uncompressed 32-bit icon image (BGRA, bottom-up, with its AND mask): what every Windows icon loader reads."""
    size = image.width
    pixels = image.load()
    rows = []
    for y in reversed(range(size)):
        rows.append(b"".join(struct.pack("BBBB", b, g, r, a) for r, g, b, a in (pixels[x, y] for x in range(size))))
    mask_stride = ((size + 31) // 32) * 4
    mask = bytes(mask_stride * size)  # all 0: the alpha channel decides
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(b"".join(rows)) + len(mask), 0, 0, 0, 0)
    return header + b"".join(rows) + mask


def write_ico(path, images):
    entries, blobs = [], []
    offset = 6 + 16 * len(images)
    for image in images:
        blob = dib(image)
        side = image.width if image.width < 256 else 0
        entries.append(struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(blob), offset))
        blobs.append(blob)
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(blobs))


def main():
    for background in BACKGROUNDS:
        if contrast(ACCENT, background) < 3:
            sys.exit(f"{ACCENT} has less than 3:1 against {background}")
    os.makedirs(OUT, exist_ok=True)
    svgs = {path: fetch(path, sha256) for path, sha256 in SOURCES.items()}
    for name, variant in ICONS:
        write_ico(os.path.join(OUT, name + ".ico"), [render(svgs[source_for(name, variant, s)], variant, s) for s in SIZES])
    with open(os.path.join(OUT, "LICENSE-fluentui-system-icons.txt"), "wb") as f:
        f.write(fetch(*LICENCE))
    print(f"Wrote {len(ICONS)} icons to {OUT}")


if __name__ == "__main__":
    main()
