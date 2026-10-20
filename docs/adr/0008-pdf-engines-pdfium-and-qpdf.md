# ADR-0008: PDF with a current pdfium and qpdf, fetched by pinned hash

## Status

Accepted

## Date

2026-10-20

## Context

v0.2 adds PDF operations (docs/specs/pdf.md). PDFs arrive from strangers by chat and email, so whatever parses them must be maintained and patched. Flevert is MIT and links nothing under the GPL or AGPL. Binaries must never be committed (CLAUDE.md), and releases are built only by GitHub Actions (ADR-0005).

Options checked on 2026-10-20:

- Ghostscript, MuPDF, iText: AGPL. PDFsharp: AGPL from 6.5; earlier versions are MIT but unmaintained.
- Magick.NET reads PDF only through Ghostscript, and its PDF writer re-encodes images.
- `Docnet.Core` (last release 2023-09) and `PDFiumCore` (around Chromium 4688, 2021) are MIT/Apache wrappers that bundle pdfium builds years behind on security fixes.
- `bblanchon/pdfium-binaries` publishes pdfium builds for each Chromium branch (MIT build scripts, pdfium BSD-3-Clause with its third-party licences in the archive). pdfium can render, extract text and images, create documents (`fpdf_edit.h`), and save; it cannot encrypt.
- qpdf (Apache-2.0) does structural work (rotate, merge, split, compress, remove metadata, encrypt and decrypt, repair) from the command line. Its Windows build links OpenSSL and ships the MSVC runtime; its zip has no licence files.

## Decision

- **pdfium** `chromium/8100` (`pdfium-win-x64.tgz`) runs in-process through a small `LibraryImport` wrapper written for Flevert, behind a process-wide lock (pdfium is not thread-safe), for rendering, images to PDF, text and image extraction and image recompression.
- **qpdf** 12.4.2 (`qpdf-12.4.2-msvc64.zip`) runs as a separate process through `ExternalTool` for structure and encryption. Passwords reach it only through standard input (`@-` for `--encrypt`, `--password-file=-` for decrypting), never the command line.
- `scripts/deps.json` pins each download (both release assets, qpdf's `LICENSE.txt` and `NOTICE.md` at tag `v12.4.2`, OpenSSL 3's `LICENSE.txt`) by SHA256; GitHub publishes a digest for release assets, and the pins match it. `scripts/fetch-deps.ps1` downloads into a fresh temporary folder, checks the hash before unpacking, keeps only listed files and changes `deps/` only after everything succeeded. CI, release and `setup-dev.ps1` run it; there is no cache, since the downloads take seconds and are checked every time.
- The installer ships `pdfium.dll`, `qpdf.exe`, `qpdf30.dll` and the MSVC runtime DLLs, and their licences go into `THIRD-PARTY-NOTICES.txt` (zlib and libjpeg-turbo, also linked into qpdf, are covered by the same licence texts pdfium ships).
- Neither binary is hash-checked at run time: the install folder is already checked to be writable only by the user and administrators, the same trust as `flevert.exe`.

## Consequences

- Updating pdfium or qpdf is a change to `deps.json` (new URL and digest) plus a test run; Dependabot cannot watch these, so RELEASING gets a monthly check for new versions.
- The installer grows by about 16 MB unpacked.
- `qpdf.exe` and `pdfium.dll` are unsigned, like Flevert; Smart App Control may block them until releases are signed (ADR-0005).
- A pdfium crash takes `flevert.exe` down with it (in-process); qpdf failures cannot. Hardening fixtures (malicious PDFs) are part of the plan before release.
