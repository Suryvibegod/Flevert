r"""Makes the right-click menu and PDF tools icons in assets/menu/ from Microsoft's Fluent UI System Icons (MIT).

The .ico files are committed, like assets/app.ico; nothing here ships. Each SVG is downloaded from a pinned commit of
https://github.com/microsoft/fluentui-system-icons and checked against its SHA256 before use. Filled icons are tinted
with ACCENT (readable on the light and the dark menu); Color icons keep their own colours. Every .ico holds 16, 20, 24,
32, 40 and 48 px, drawn from the Fluent size that fits best (a size that divides it, else the next larger), so the menu
is sharp at every DPI. Also writes the Fluent licence beside them (THIRD-PARTY-NOTICES, scripts/deps.json).

Usage, from the repository root, in a throw-away virtual environment outside the repository:
    python -m venv %TEMP%\flevert-icons
    %TEMP%\flevert-icons\Scripts\python -m pip install resvg-py==0.5.0 pillow==13.0.0
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

# Fluent name and variant. The file is assets/menu/<name>.ico; MenuOperations and the PDF tools window use the name.
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
    ("toolbox", "color"),                  # More PDF tools…
    # The PDF tools window.
    ("document_dismiss", "filled"),        # Remove blank pages
    ("document_one_page_columns", "filled"),  # 2 pages per sheet
    ("grid", "filled"),                    # 4 pages per sheet
    ("document_fit", "filled"),            # Fit pages to A4
    ("arrow_split", "filled"),             # Split into parts under N MB
    ("draw_text", "filled"),               # Add watermark
    ("document_page_bottom_center", "filled"),  # Add page numbers
    ("layer", "filled"),                   # Flatten forms and comments
    ("document_text_extract", "filled"),   # Extract text
    ("image_multiple", "filled"),          # Extract images
    ("document", "color"),                 # any tool whose icon is not here
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
    "assets/Document One Page Columns/SVG/ic_fluent_document_one_page_columns_20_filled.svg": "5bd5356c7643b0fd42adc6d8d9769926cf406b81e21179825901b444ada45221",
    "assets/Document One Page Columns/SVG/ic_fluent_document_one_page_columns_24_filled.svg": "bb96adcbc71e1c13f484537f1c73bdfecc2447ae0d52df343f4a61ad145bb4c5",
    "assets/Grid/SVG/ic_fluent_grid_16_filled.svg": "0c6f587cf8b463a79dbddeb2218f1aad870a4a5ec8e3856779942326c8cf77dd",
    "assets/Grid/SVG/ic_fluent_grid_20_filled.svg": "f89f679798e60fc3ea13a42e1ec70e6cdb65958b8a84d73819a22257d75c1bf3",
    "assets/Grid/SVG/ic_fluent_grid_24_filled.svg": "ca423fb466fa08a32a38ad28ab689bd25d3809b70cdecb719bdf0c052bc843dd",
    "assets/Document Fit/SVG/ic_fluent_document_fit_16_filled.svg": "86a1f8ca44f83e483233d656206bddf11e500c1081fa7fcf8a6aec6e86133b4c",
    "assets/Document Fit/SVG/ic_fluent_document_fit_20_filled.svg": "d8bc7c8367e42bc52c2a67ef1bbd819e77d4a0883a1f8f0fb10fab297e038eb4",
    "assets/Document Fit/SVG/ic_fluent_document_fit_24_filled.svg": "bd38eb6ff95b1434dee90c334c5919dada2140181fad2718877fff03300f6cc2",
    "assets/Arrow Split/SVG/ic_fluent_arrow_split_16_filled.svg": "3560fec50e14f063dc6b4871bf3958a8f7503c318883ccce273a96222240d98d",
    "assets/Arrow Split/SVG/ic_fluent_arrow_split_20_filled.svg": "db71e2dbb15f681e35c4ef5b999f39a9e4f6bc741d2e8577271bcd9731c5d765",
    "assets/Arrow Split/SVG/ic_fluent_arrow_split_24_filled.svg": "b9568e92dff49fa711c1ce52de926bbb19a317a551f6491a30c6875d7bf8d83c",
    "assets/Draw Text/SVG/ic_fluent_draw_text_20_filled.svg": "ab34cc4410f3962918e3318607e6bd561a74bc0cd2a4d0c6881c9a675f724a2e",
    "assets/Draw Text/SVG/ic_fluent_draw_text_24_filled.svg": "cccf2b3c8fd95799ea777cbb46abb6c42c0f2610f3509f97007c087d7b81c5e7",
    "assets/Document Page Bottom Center/SVG/ic_fluent_document_page_bottom_center_20_filled.svg": "b4f5a728386c2a001125afab63bfd25cc19b52f0abafebde3dba313a9b9ceb54",
    "assets/Document Page Bottom Center/SVG/ic_fluent_document_page_bottom_center_24_filled.svg": "7b4e01a30fb88531cb42fe2ad1db2da8a1990e32615236061bc79c2de1b13c93",
    "assets/Layer/SVG/ic_fluent_layer_20_filled.svg": "e49594a1aec88b5ccde1e75423971691c7fcaac8fbb9aede0ccbc94957db2162",
    "assets/Layer/SVG/ic_fluent_layer_24_filled.svg": "fd4ccc33686ae06945c60d238347140eff9e8a7ab06737083f7a73412fe4084f",
    "assets/Document Text Extract/SVG/ic_fluent_document_text_extract_16_filled.svg": "91f998985468d115772dd6a8aa157d145c9ff49959dbe36bfc56b608bd946121",
    "assets/Document Text Extract/SVG/ic_fluent_document_text_extract_20_filled.svg": "e6aba92e560aa870f7ccc596754fb59269dd4e946cb8da6a812399899cc7b2a8",
    "assets/Document Text Extract/SVG/ic_fluent_document_text_extract_24_filled.svg": "a5ad84a0ff17747e7c7ac8da4db333f679a9c5b46c75db000f0b63aa8f473093",
    "assets/Document Text Extract/SVG/ic_fluent_document_text_extract_32_filled.svg": "1aa8520b26efb658d57082799641cd2ae0d2ed4d125d55b616b2fdfe9d565910",
    "assets/Document Text Extract/SVG/ic_fluent_document_text_extract_48_filled.svg": "fd4b62ebee780f30d64caa15ee17445f7b5b96ef05dc512cb1038c63a58aa377",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_16_filled.svg": "fe5ebb1e154264b9736ce4a23754d9fcd4e19b8df4a4e3369a9c45d7f1438b88",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_20_filled.svg": "102f8a7e476ed0c49dbb28ac9837546ce4a64cdb78957c963db70ca7af773291",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_24_filled.svg": "095e0446adef1be2e40b0bb79548109398e45d478fbaed2e6d07e52c6cd8d717",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_32_filled.svg": "16d2103eef3fff8f99543d1b460e6aff7643c21dd012139345ad87da3bacbe86",
    "assets/Image Multiple/SVG/ic_fluent_image_multiple_48_filled.svg": "45ed2703baff95e741edfb73d9f39d16863a5061ae7fb1212248241610290ccb",
    "assets/Document/SVG/ic_fluent_document_16_color.svg": "625602d2d144d041e97ad6b18d7dcfcbea930b7f05237112656cc7554d0fd03e",
    "assets/Document/SVG/ic_fluent_document_20_color.svg": "71107a8b524eb835a5612be0d75e507db6a4198447eb105761580dfb6873fecb",
    "assets/Document/SVG/ic_fluent_document_24_color.svg": "3219a365c3b637a5ae5f695ee541189236e8780696507e78a811bd3684eee5ee",
    "assets/Document/SVG/ic_fluent_document_32_color.svg": "502bc697023901d3d726009ab3f51695a598f85d09aa6fa16c5ef1080e368819",
    "assets/Document/SVG/ic_fluent_document_48_color.svg": "806eaefed45088137496309ce60c9b5d994059f55ed386f92e323ed4752d5d0c",
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
