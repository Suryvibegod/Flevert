"""Writes two TIFF fixtures as document scanners and cameras produce them (Pillow; synthetic content):

mixed-g4-colour.tif   page 1: black-and-white text page, Group 4; page 2: colour page, LZW. Scanners with automatic
                      colour detection mix the two in one file.
with-thumbnail.tif    a 300x200 image and a reduced-resolution 75x50 thumbnail (NewSubfileType = 1), which is not a
                      second page.
"""
import os
import sys
from PIL import Image, ImageDraw, TiffImagePlugin

out = sys.argv[1]

text = Image.new("1", (200, 150), 1)
ImageDraw.Draw(text).text((20, 20), "FAX COVER", fill=0)
colour = Image.new("RGB", (200, 150), (240, 240, 240))
ImageDraw.Draw(colour).rectangle([40, 40, 160, 110], fill=(220, 40, 40))
with TiffImagePlugin.AppendingTiffWriter(os.path.join(out, "mixed-g4-colour.tif"), True) as tf:
    text.save(tf, compression="group4")
    tf.newFrame()
    colour.save(tf, compression="tiff_lzw")
    tf.newFrame()

photo = Image.new("RGB", (300, 200), (30, 90, 160))
ImageDraw.Draw(photo).ellipse([60, 40, 240, 160], fill=(250, 200, 40))
with TiffImagePlugin.AppendingTiffWriter(os.path.join(out, "with-thumbnail.tif"), True) as tf:
    photo.save(tf, compression="tiff_lzw")
    tf.newFrame()
    photo.resize((75, 50)).save(tf, compression="tiff_lzw", tiffinfo={254: 1})
    tf.newFrame()
print("ok")
