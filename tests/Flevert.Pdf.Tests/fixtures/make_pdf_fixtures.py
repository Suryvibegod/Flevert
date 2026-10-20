"""Writes the PDF test fixtures by hand, with the Python standard library only.

Usage: python make_pdf_fixtures.py <output folder>

The files are byte-for-byte reproducible: no dates, no IDs, no compression.
password-protected.pdf is made from three-colours.pdf by qpdf (see README.md).
"""

import pathlib
import sys


def write_pdf(path, pages):
    """pages: list of (width_pt, height_pt, content_stream_bytes)."""
    objects = []  # bodies, object number = index + 1

    def add(body):
        objects.append(body)
        return len(objects)

    catalog = add(None)
    page_tree = add(None)
    kids = []
    for width, height, content in pages:
        stream = add(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
        kids.append(add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 %s %s] /Resources << >> /Contents %d 0 R >>"
                        % (page_tree, number(width), number(height), stream)))
    objects[catalog - 1] = b"<< /Type /Catalog /Pages %d 0 R >>" % page_tree
    objects[page_tree - 1] = b"<< /Type /Pages /Kids [%s] /Count %d >>" % (
        b" ".join(b"%d 0 R" % kid for kid in kids), len(kids))

    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for index, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % index + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    for offset in offsets:
        out += b"%010d 00000 n \n" % offset
    out += b"trailer\n<< /Size %d /Root %d 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, catalog, xref)
    pathlib.Path(path).write_bytes(bytes(out))


def number(value):
    return (b"%d" % value) if value == int(value) else (b"%g" % value)


def fill(r, g, b, x, y, width, height):
    """A rectangle in a solid DeviceRGB colour, components 0..1."""
    return b"%s %s %s rg %s %s %s %s re f" % (number(r), number(g), number(b), number(x), number(y), number(width), number(height))


def main(folder):
    folder = pathlib.Path(folder)
    # Three pages, each one solid colour and a different size: A4 portrait red, Letter portrait green,
    # 400 x 300 landscape blue.
    write_pdf(folder / "three-colours.pdf", [
        (595, 842, fill(1, 0, 0, 0, 0, 595, 842)),
        (612, 792, fill(0, 1, 0, 0, 0, 612, 792)),
        (400, 300, fill(0, 0, 1, 0, 0, 400, 300)),
    ])
    # 200 x 2 inches (14400 pt is the largest page PDF allows): left half red, right half blue. At 150 DPI it
    # would be 30000 px wide, over the 10000 px limit.
    write_pdf(folder / "huge-page.pdf", [
        (14400, 144, fill(1, 0, 0, 0, 0, 7200, 144) + b"\n" + fill(0, 0, 1, 7200, 0, 7200, 144)),
    ])


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
