"""Makes portrait-gps.heic: a phone-style portrait photo with a GPS location.

The pixels are stored 320x240 (landscape) with EXIF Orientation 6, the way phones store portrait shots, plus GPS, camera
make and model. Upright, the image is 240x320 with the white block at the top-left. Synthetic; no third-party content.
Usage: python make_portrait_gps_heic.py portrait-gps.heic
"""
import sys

import pillow_heif
from PIL import Image, ImageDraw

pillow_heif.register_heif_opener()
out = sys.argv[1]

# Draw the upright picture (240 wide, 320 tall), white block at the top-left, then rotate it the way a phone sensor
# stores it; Orientation 6 tells viewers to turn it back 90 degrees clockwise.
upright = Image.new("RGB", (240, 320))
px = upright.load()
for y in range(320):
    for x in range(240):
        px[x, y] = (x * 255 // 240, y * 255 // 320, 128)
ImageDraw.Draw(upright).rectangle([10, 10, 90, 70], fill=(255, 255, 255))
stored = upright.rotate(90, expand=True)  # 320x240, as the sensor stores a portrait shot

exif = Image.Exif()
exif[0x010F] = "SecretPhoneMake"  # Make
exif[0x0110] = "SecretPhoneModel"  # Model
exif[0x0112] = 6  # Orientation: rotate 90 degrees clockwise to view
gps = {
    1: "N",  # GPSLatitudeRef
    2: (48.0, 51.0, 30.0),  # GPSLatitude (the Eiffel Tower: a public landmark)
    3: "E",  # GPSLongitudeRef
    4: (2.0, 17.0, 40.0),  # GPSLongitude
}
exif[0x8825] = gps  # GPSInfo IFD
# pillow-heif writes the orientation as a HEIF rotation (irot) and keeps Orientation 6 in the EXIF, as iPhones do.
# Having both is what makes a double rotation possible in a careless decoder, so the file must keep both.
stored.save(out, format="HEIF", quality=80, exif=exif.tobytes())
data = open(out, "rb").read()
if b"irot" not in data or b"\x01\x12\x00\x03\x00\x00\x00\x01\x00\x06" not in data:
    raise SystemExit("expected both an irot box and EXIF Orientation 6")
print(pillow_heif.__version__, pillow_heif.libheif_info())
