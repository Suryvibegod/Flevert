# Test fixtures

| File | Origin |
|---|---|
| `three-colours.pdf` | Written by hand by `make_pdf_fixtures.py` (Python standard library only, no compression): page 1 A4 portrait (595×842 pt) solid red, page 2 Letter portrait (612×792 pt) solid green, page 3 400×300 pt landscape solid blue. Synthetic. Released under the repository's MIT license. |
| `huge-page.pdf` | Written by `make_pdf_fixtures.py`: one page 14400×144 pt (200×2 inches, the largest page size PDF allows), left half red, right half blue. At 150 DPI it would be 30000 px wide, over the 10000 px render limit. Synthetic. MIT. |
| `password-protected.pdf` | `three-colours.pdf` encrypted by qpdf 12.4.2 (from `scripts/fetch-deps.ps1`) with AES-256, user and owner password `flevert-test`. AES uses a random salt, so regenerating gives different bytes with the same content. MIT. |

Regenerate with:

```
python make_pdf_fixtures.py .
..\..\..\deps\qpdf\bin\qpdf.exe --encrypt --user-password=flevert-test --owner-password=flevert-test --bits=256 -- three-colours.pdf password-protected.pdf
```

The 2 001-page document, the non-PDF file and the copies at Unicode and long paths are made at run time by the tests themselves (`TestPdf.cs`).
