# Changelog

All notable changes to Flevert are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

Video and audio: convert, compress to a size that goes through e-mail, and take the sound out of a video, on your PC.

### Added

- Right-click a video (`.mp4`, `.mov`, `.m4v`, `.mkv`, `.webm`, `.avi`, `.wmv`, `.3gp`, `.mts`, `.m2ts`) and choose **Flevert**:
  - **Compress**: Light (best quality), Balanced or Strong (smallest), or **Under 10 MB**, **Under 25 MB** or **Under 100 MB**, which are always smaller than that size, or say the video is too long to fit. The notification says the new size and the old one.
  - **Convert to** MP4 or MOV (H.264 and AAC, which play everywhere), WEBM (VP9 and Opus), or GIF (at most 480 pixels and the first 30 seconds). Phone videos come out upright, and iPhone HDR videos keep their colours.
  - **Extract audio** as MP3 or M4A (an AAC sound is copied as it is).
  - **Remove hidden info**: a copy without where it was filmed, the camera or the software, made in seconds, with the picture and sound untouched.
- Right-click an audio file (`.mp3`, `.wav`, `.m4a`, `.aac`, `.flac`, `.ogg`, `.opus`, `.wma`, `.aiff`, `.aif`) and choose **Flevert > Convert to** MP3, M4A, WAV, FLAC, OGG or Opus.
- Video and audio keep their title, artist and recording date, never the place they were recorded. A converted video keeps its first sound track, and the notification says when there were others. The progress window shows how far the current file is, as a percentage.
- The installer includes ffmpeg, which is free software under the GNU GPL: each release has its complete source as `ffmpeg-source-<build>.tar` beside the setup.

## [0.2.0] - 2026-10-20

PDFs: turn photos into one, and convert, merge, split, rotate, compress or password-protect PDF files.

### Added

- Right-click pictures and choose **Flevert > Combine into PDF**: one PDF with a page per picture, in name order (`photo 2` before `photo 10`), each turned the right way up. JPEG photos are embedded without re-encoding, minus their GPS location and camera details. The PDF's only metadata is "Producer: Flevert".
- Right-click a `.pdf` and choose **Flevert**:
  - **Convert** to JPG pages or PNG pages: a folder with one picture per page, at 150 DPI.
  - **Merge PDFs** (two or more, in name order) and **Split into pages** (a folder with one PDF per page).
  - **Rotate** every page 90° right, 90° left or 180°.
  - **Compress** without losing quality, and **Remove metadata** (author, dates, XMP, attachments, scripts).
  - **Protect with password** (AES-256) and **Remove password** (when you know it). Passwords are never written to disk or logs.
- **Combine into PDF** and **Merge PDFs** wait for every selected file. If one arrives too late, Flevert reports it rather than leaving it out of the result without a word.
- The progress window counts pages inside a long file ("page 37 of 200"), and its bar moves while that file is processed.
- The installer includes the licences of pdfium, qpdf and the libraries they bundle (OpenSSL, zlib, libjpeg-turbo, Microsoft Visual C++ runtime).

### Changed

- HEIC and HEIF photos no longer offer **Convert > GIF**. Explorer shows at most 16 entries in a menu like Flevert's, and with **Combine into PDF** added, **Remove metadata** would have been the one hidden.

### Security

- PDFs are read by pdfium in a separate process that has a memory cap and a time limit per page and cannot start other programs; qpdf runs as its own limited process too. A damaged or hostile PDF fails on its own, and the rest of the batch still finishes. That process limits crashes, time and memory, but it is not a sandbox: it can still reach your files like any program you run.

### Fixed

- Starting two different picture actions at the same moment (for example **Convert > PNG** on some files and **Compress** on others) no longer makes one of them fail with "Access to the path is denied". This also affected v0.1.0.

### Known issues

- The TIFF issue listed for 0.1.0 remains: Flevert still bundles ImageMagick 7.1.2-33 ([GHSA-g5j8-ff59-wp88](https://github.com/ImageMagick/ImageMagick/security/advisories/GHSA-g5j8-ff59-wp88), low severity).
- Releases are still not code-signed, so Windows Smart App Control may block them (see the README).
- On slow PCs, very heavy scanned pages (JPEG 2000 or JBIG2) may be refused as taking too long to convert to pictures.
- Rotate, merge and split make a new PDF, so a digital signature in the original is not valid in the result: Flevert says so when it happens.

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

[Unreleased]: https://github.com/Suryvibegod/Flevert/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/Suryvibegod/Flevert/releases/tag/v0.2.0
