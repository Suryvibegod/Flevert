# Test fixtures

| File | Origin |
|---|---|
| `three-colours.pdf` | Written by hand by `make_pdf_fixtures.py` (Python standard library only, no compression): page 1 A4 portrait (595×842 pt) solid red, page 2 Letter portrait (612×792 pt) solid green, page 3 400×300 pt landscape solid blue. Synthetic. Released under the repository's MIT license. |
| `huge-page.pdf` | Written by `make_pdf_fixtures.py`: one page 14400×144 pt (200×2 inches, the largest page size PDF allows), left half red, right half blue. At 150 DPI it would be 30000 px wide, over the 10000 px render limit. Synthetic. MIT. |
| `rotations.pdf` | Written by `make_pdf_fixtures.py`: four 400×300 pt pages, white with a red 100×100 pt square in the top-left corner of the page as drawn, with `/Rotate` 0, 90, 180 and 270. On screen the square sits top-left, top-right, bottom-right and bottom-left, so a test can see where a rotation moved it. Synthetic. MIT. |
| `metadata.pdf` | Written by `make_pdf_fixtures.py`: one 400×300 pt page (white, a blue rectangle, a 2×2 image) carrying everything Remove metadata takes out: document information (Title, Author, Subject, Keywords, Creator, Producer, both dates and a custom `/Company`); XMP on the catalog, the page and the image; `/PieceInfo` on the catalog and the page; an attachment `secret.txt` in the embedded files name tree, also shown by a hidden file attachment annotation; document JavaScript in the names tree, a JavaScript `/OpenAction`, catalog `/AA` (on close), page `/AA` (on open, with a chained `/Next`, and a Rendition action with `/JS` on close) and a link whose action is JavaScript. The link's `/Contents` is UTF-16 text outside ASCII ("Liên kết" and U+1F517). It also has a bookmark that must stay. Names and dates are invented. Synthetic. MIT. |
| `password-protected.pdf` | `three-colours.pdf` encrypted by qpdf 12.4.2 (from `scripts/fetch-deps.ps1`) with AES-256, user and owner password `flevert-test`. AES uses a random salt, so regenerating gives different bytes with the same content. MIT. |

Regenerate with:

```
python make_pdf_fixtures.py .
..\..\..\deps\qpdf\bin\qpdf.exe --encrypt --user-password=flevert-test --owner-password=flevert-test --bits=256 -- three-colours.pdf password-protected.pdf
```

The 2 001-page document, the document information written inside a trailer, the catalog nested 490 deep, the 200 000- and 500 000-page documents that keep qpdf busy for the cancel and kill tests, the non-PDF file and the copies at Unicode and long paths are made at run time by the tests themselves (`TestPdf.cs`).
. So are the malicious pages of the PDF worker tests: `WriteStallingPage` (one A4 page whose shading is a million page-sized triangles, 60 KB, about 40 minutes of pdfium work) and `WriteSquarePage` (a 14 400 pt square page, 400 MB of pixels at 10 000 px).
