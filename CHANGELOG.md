# Changelog

All notable changes to Flevert are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

PDF tools, right from the menu.

### Added

- Right-click pictures and choose **Flevert > Combine into PDF**: one PDF with a page per picture, in name order (`photo 2` before `photo 10`), turned the right way up. JPEG photos go in unchanged, without their GPS location or camera details; the PDF itself carries no metadata but "Producer: Flevert".
- Right-click a `.pdf` and choose **Flevert**:
  - **Convert** to JPG pages or PNG pages: a folder with one picture per page, at 150 DPI.
  - **Merge PDFs** (two or more, in name order) and **Split into pages** (a folder with one PDF per page).
  - **Rotate** every page 90° right, 90° left or 180°.
  - **Compress** without losing quality, and **Remove metadata** (author, dates, XMP, attachments, scripts).
  - **Protect with password** (AES-256) and **Remove password** (when you know it). Passwords are never written to disk or logs.
- The progress window counts pages inside a long file ("page 37 of 200") and its bar moves within one file.

### Changed

- HEIC and HEIF photos no longer offer **Convert > GIF**: with **Combine into PDF** added, Explorer would have hidden **Remove metadata** (it shows at most 16 entries in a menu like Flevert's).
- Selecting many files for one result (Combine into PDF, Merge PDFs) waits a little longer for the last of them, and never makes a result with a file silently left out: a file that arrives too late is reported.

### Security

- PDFs are read with a current pdfium in a separate process with a memory cap, a time limit per page and no rights to start anything; qpdf also runs as its own limited process. A damaged or hostile PDF fails on its own, and the rest of the batch still finishes.
- The installer now includes the licences of pdfium, qpdf and the libraries they bundle (OpenSSL, zlib, libjpeg-turbo, Microsoft Visual C++ runtime).

### Fixed

- Starting two different picture actions at the same moment (for example **Convert > PNG** on some files and **Compress** on others) no longer makes one of them fail with "Access to the path is denied". This also affected v0.1.0.

## [0.1.0] - 2026-10-20

The first release: pictures, and an installer.

### Added

- Right-click any `.jpg .jpeg .png .webp .heic .heif .avif .gif .bmp .tif .tiff` file in Windows Explorer and choose **Flevert**:
  - **Convert** to JPG, PNG, WebP, AVIF, GIF, BMP or TIFF. iPhone photos (HEIC) open anywhere as JPG, turned the right way up; animations stay animated in GIF and WebP.
  - **Compress** at three levels, or to fit **under 500 KB, 1 MB or 5 MB**.
  - **Remove metadata**: camera details, GPS location, XMP, IPTC and comments, keeping the colour profile.
- Select many files at once: they are processed as one batch, several at a time on PCs with more than two cores, with one notification at the end. Batches that take more than two seconds show a progress window with **Cancel**.
- Results are saved next to the original, which is never changed or overwritten; names never clash (`photo (1).jpg`).
- A per-user installer that needs no administrator rights, with an option to bring back the classic Windows 10 style right-click menu on Windows 11. Uninstalling removes everything Flevert created.
- Works offline: no account, no telemetry. The only network use is a check for a new version on GitHub, at most once a day after a conversion; when one is out, Flevert offers to open its download page (Download, Later, Skip this version). Nothing is downloaded or installed by itself. Setup has a check box to turn it off, as does `flevert.exe update-check off`.
- The progress window works with screen readers (named progress bar and Cancel button; Esc cancels).

### Security

- Images are decoded by ImageMagick with an allow-list of formats, resource limits and a fixed policy that Flevert checks before every run.
- Setup installs only into a folder no other Windows account can change, and checks it before copying anything.
- Releases are built by GitHub Actions with a build provenance attestation and a `SHA256SUMS.txt` (see the README for how to verify a download). They are not code-signed yet.

### Known issues

- A specially crafted TIFF file can make the bundled ImageMagick 7.1.2-33 overflow its stack ([GHSA-g5j8-ff59-wp88](https://github.com/ImageMagick/ImageMagick/security/advisories/GHSA-g5j8-ff59-wp88), low severity): Flevert then closes, and the rest of that batch is not converted. No code runs. It will be fixed by updating once a Magick.NET release includes ImageMagick 7.1.2-34.
- Windows Smart App Control may block this unsigned release (see the README).

[Unreleased]: https://github.com/Suryvibegod/Flevert/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Suryvibegod/Flevert/releases/tag/v0.1.0
