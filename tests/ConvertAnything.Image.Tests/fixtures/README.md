# Test fixtures

| File | Origin |
|---|---|
| `gradient-320x240.heic` | Generated for this project by `make_heic_fixture.py` (pillow-heif 1.8.1, libheif 1.23.6, x265). A synthetic 320×240 gradient with a white block at the top-left; no third-party image content. Released under the repository's MIT license. |
| `wide-50000x1.png` | A solid grey 50000×1 PNG (356 bytes) written by Magick.NET without resource limits, to prove the width limit. MIT. |

Regenerate with:

```
python -m pip install pillow pillow-heif
python make_heic_fixture.py gradient-320x240.heic
```

Every other test image is generated at run time by the tests themselves.
