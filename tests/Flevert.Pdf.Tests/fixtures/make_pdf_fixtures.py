"""Writes the PDF test fixtures by hand, with the Python standard library only.

Usage: python make_pdf_fixtures.py <output folder>

The files are byte-for-byte reproducible: no dates, no IDs, no compression.
password-protected.pdf is made from three-colours.pdf by qpdf (see README.md).
"""

import pathlib
import sys


def write_pdf(path, pages):
    """pages: list of (width_pt, height_pt, content_stream_bytes) or (width_pt, height_pt, content, rotate)."""
    objects = []  # bodies, object number = index + 1

    def add(body):
        objects.append(body)
        return len(objects)

    catalog = add(None)
    page_tree = add(None)
    kids = []
    for width, height, content, *rotate in pages:
        stream = add(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
        rotation = b" /Rotate %d" % rotate[0] if rotate else b""
        kids.append(add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 %s %s]%s /Resources << >> /Contents %d 0 R >>"
                        % (page_tree, number(width), number(height), rotation, stream)))
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


def write_objects(path, objects, trailer=b""):
    """objects: bodies in order, object number = index + 1, object 1 the catalog. trailer: extra trailer entries."""
    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for index, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % index + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    for offset in offsets:
        out += b"%010d 00000 n \n" % offset
    out += b"trailer\n<< /Size %d /Root 1 0 R%s >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, trailer, xref)
    pathlib.Path(path).write_bytes(bytes(out))


def stream(dictionary, data):
    return b"<< %s /Length %d >>\nstream\n" % (dictionary, len(data)) + data + b"\nendstream"


def xmp(author):
    return (b'<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>\n'
            b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
            b'<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">'
            b'<dc:creator><rdf:Seq><rdf:li>' + author + b'</rdf:li></rdf:Seq></dc:creator>'
            b'</rdf:Description></rdf:RDF></x:xmpmeta>\n<?xpacket end="w"?>')


def write_metadata_pdf(path):
    """One page with everything Remove metadata takes out (README.md lists each item and its object)."""
    content = (fill(1, 1, 1, 0, 0, 400, 300) + b"\n" + fill(0, 0, 1, 50, 50, 300, 200)
               + b"\nq 100 0 0 100 150 100 cm /Im1 Do Q")
    objects = [
        # 1 catalog: XMP, names (attachment, document JavaScript), JavaScript open action, JavaScript on close
        b"<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R /Names << /EmbeddedFiles 7 0 R /JavaScript 10 0 R >>"
        b" /OpenAction 11 0 R /AA << /WC << /S /JavaScript /JS (app.alert\\('closing'\\)) >> >>"
        b" /PieceInfo << /Flevert << /LastModified (D:20240102030405Z) /Private (catalog piece) >> >>"
        b" /Outlines 16 0 R >>",
        # 2 page tree
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        # 3 page: its own XMP, a JavaScript action on open, private data, an attachment and a JavaScript link
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /XObject << /Im1 13 0 R >> >>"
        b" /Contents 5 0 R /Metadata 6 0 R /AA << /O 12 0 R /C << /S /Rendition /OP 0 /JS (app.alert\\('rendition'\\)) >> >>"
        b" /PieceInfo << /Flevert << /Private (page piece) >> >>"
        b" /Annots [14 0 R 15 0 R] >>",
        # 4 document XMP
        stream(b"/Type /Metadata /Subtype /XML", xmp(b"Catalog XMP Author")),
        # 5 page content: white, a blue rectangle, the image in the middle
        stream(b"", content),
        # 6 page XMP
        stream(b"/Type /Metadata /Subtype /XML", xmp(b"Page XMP Author")),
        # 7 embedded files name tree
        b"<< /Names [(secret.txt) 8 0 R] >>",
        # 8 file specification of the attachment
        b"<< /Type /Filespec /F (secret.txt) /UF (secret.txt) /EF << /F 9 0 R >> >>",
        # 9 the attached file
        stream(b"/Type /EmbeddedFile", b"Attached secret text"),
        # 10 document JavaScript name tree
        b"<< /Names [(init) << /S /JavaScript /JS (app.alert\\('document script'\\)) >>] >>",
        # 11 open action
        b"<< /S /JavaScript /JS (app.alert\\('open action'\\)) >>",
        # 12 page open action, chained to a second script
        b"<< /S /JavaScript /JS (app.alert\\('page open'\\)) /Next << /S /JavaScript /JS (app.alert\\('next'\\)) >> >>",
        # 13 a 2 x 2 image with its own XMP: red, green / blue, white
        stream(b"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Metadata 17 0 R",
               b"\xff\x00\x00\x00\xff\x00\x00\x00\xff\xff\xff\xff"),
        # 14 attachment annotation, hidden so that it draws nothing
        b"<< /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20] /F 2 /FS 8 0 R /Contents (attached) >>",
        # 15 link whose action is JavaScript, described in UTF-16 text outside ASCII ("Liên kết" and U+1F517) that
        # must come through the rewrite unchanged
        b"<< /Type /Annot /Subtype /Link /Rect [50 50 350 250] /Border [0 0 0] /A << /S /JavaScript /JS (app.alert\\('link'\\)) >>"
        b" /Contents <FEFF004C006900EA006E0020006B1EBF00740020D83DDD17> >>",
        # 16 an outline (bookmarks) the user wants to keep
        b"<< /Type /Outlines /First 18 0 R /Last 18 0 R /Count 1 >>",
        # 17 image XMP
        stream(b"/Type /Metadata /Subtype /XML", xmp(b"Image XMP Author")),
        # 18 the bookmark
        b"<< /Title (Chapter 1) /Parent 16 0 R /Dest [3 0 R /Fit] >>",
        # 19 document information
        b"<< /Title (Secret plan) /Author (Nguyen Van A) /Subject (Taxes) /Keywords (private) /Creator (Writer)"
        b" /Producer (Some PDF Library 1.0) /CreationDate (D:20240101090000+07'00') /ModDate (D:20240102090000+07'00')"
        b" /Company (Example Ltd) >>",
    ]
    write_objects(path, objects, trailer=b" /Info 19 0 R")


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
    # Four 400 x 300 pages, white with a red 100 x 100 square in the top-left corner of the page as drawn, and
    # /Rotate 0, 90, 180 and 270: shown on screen, the square sits top-left, top-right, bottom-right and bottom-left.
    mark = fill(1, 1, 1, 0, 0, 400, 300) + b"\n" + fill(1, 0, 0, 0, 200, 100, 100)
    write_pdf(folder / "rotations.pdf", [(400, 300, mark, rotate) for rotate in (0, 90, 180, 270)])
    write_metadata_pdf(folder / "metadata.pdf")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
