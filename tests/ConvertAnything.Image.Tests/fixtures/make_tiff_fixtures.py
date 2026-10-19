"""Writes TIFF fixtures as document scanners and cameras produce them (Pillow; synthetic content):

mixed-g4-colour.tif        page 1: black-and-white text page, Group 4; page 2: colour page, LZW. Scanners with automatic
                           colour detection mix the two in one file.
with-thumbnail.tif         a 300x200 image, then a reduced-resolution 75x50 thumbnail (NewSubfileType = 1).
thumbnail-first.tif        the same thumbnail stored before the image.
pages-with-thumbnails.tif  page, thumbnail, page, thumbnail: two real pages.
"""
import os
import sys
from PIL import Image, ImageDraw, TiffImagePlugin

out = sys.argv[1]


def write(name, images):
    """images: (image, compression, tiffinfo) per IFD, in order."""
    with TiffImagePlugin.AppendingTiffWriter(os.path.join(out, name), True) as tf:
        for i, (image, compression, info) in enumerate(images):
            if i:
                tf.newFrame()
            image.save(tf, compression=compression, tiffinfo=info or {})


text = Image.new("1", (200, 150), 1)
ImageDraw.Draw(text).text((20, 20), "FAX COVER", fill=0)
colour = Image.new("RGB", (200, 150), (240, 240, 240))
ImageDraw.Draw(colour).rectangle([40, 40, 160, 110], fill=(220, 40, 40))
write("mixed-g4-colour.tif", [(text, "group4", None), (colour, "tiff_lzw", None)])

photo = Image.new("RGB", (300, 200), (30, 90, 160))
ImageDraw.Draw(photo).ellipse([60, 40, 240, 160], fill=(250, 200, 40))
thumb = photo.resize((75, 50))
reduced = {254: 1}
write("with-thumbnail.tif", [(photo, "tiff_lzw", None), (thumb, "tiff_lzw", reduced)])
write("thumbnail-first.tif", [(thumb, "tiff_lzw", reduced), (photo, "tiff_lzw", None)])
write("pages-with-thumbnails.tif", [(photo, "tiff_lzw", None), (thumb, "tiff_lzw", reduced),
                                    (colour, "tiff_lzw", None), (colour.resize((50, 38)), "tiff_lzw", reduced)])
print("ok")
