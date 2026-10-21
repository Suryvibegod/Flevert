# Changelog

All notable changes to Flevert are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

Video and audio: convert, compress to a size you choose, take the sound out of a video, and remove where it was filmed, on your PC.

### Added

- Right-click a video (`.mp4`, `.mov`, `.m4v`, `.mkv`, `.webm`, `.avi`, `.wmv`, `.3gp`, `.mts`, `.m2ts`) and choose **Flevert**:
  - **Compress**: Light (best quality), Balanced or Strong (smallest), or **Under 10 MB**, **Under 25 MB** or **Under 100 MB**, which are always smaller than that size, or say the video is too long to fit. The notification says the new size and the old one.
  - **Convert to** MP4 or MOV (H.264 and AAC, which play everywhere), WEBM (VP9 and Opus), or GIF (at most 480 pixels and the first 30 seconds). Phone videos come out upright, iPhone HDR videos are converted to standard colours so they look right on every screen, and a camcorder's interlaced video (1080i) is deinterlaced.
  - **Extract audio** as MP3 or M4A (an AAC sound is copied as it is).
  - **Remove hidden info**: a copy without where it was filmed, the camera or the software, made in seconds, with the picture and sound untouched.
- Right-click an audio file (`.mp3`, `.wav`, `.m4a`, `.aac`, `.flac`, `.ogg`, `.opus`, `.wma`, `.aiff`, `.aif`) and choose **Flevert > Convert to** MP3, M4A, WAV, FLAC, OGG or Opus.
- Video and audio keep their title, artist and recording date, never the place they were recorded. A converted video keeps its first sound track, and the notification says when there were others. The progress window shows how far the current file is, as a percentage.
- The installer includes ffmpeg, which is free software under the GNU GPL: each release has its complete source beside the setup, as the files `ffmpeg-source-<build>-<n>-of-<count>.tar`.

## [1.2.3] - 2026-10-21

### Changed

- A PDF's usual right-click menu has one **Flevert** entry again. The second entry of 1.2.2 is now **Flevert: more PDF tools** and appears only when you hold **Shift** while right-clicking: Remove blank pages, Add page numbers, Print layout, Fit pages to A4, Lock filled forms, Split by size, Extract images, and Convert to PNG images (moved from Convert to). Explorer shows at most 16 entries in one menu, so not every tool fits in the first.
- **Add watermark…** moved into the **Flevert** menu, after Rotate.

## [1.2.2] - 2026-10-20

Version numbers now start at 1: this release follows 0.2.1, and the next feature release is 1.3.0.

### Added

- New PDF tools in **Flevert PDF tools**, a second entry beside **Flevert** in the PDF menu with its own list. Each runs straight from the menu, on your computer, for each PDF you select:
  - **Remove blank pages** drops the empty backs of a two-sided scan and says which pages it removed. A page with a comment or with text you can select is always kept, and so is a scanned page with any small mark, such as initials, a stamp or a pencil note; a scanner's shadow along the edge and specks of dust do not count.
  - **Add page numbers** at the bottom centre, as `1 / 12`, upright even on turned pages.
  - **Add watermark…** asks for your text once for all the PDFs you selected, and writes it, Vietnamese included, across every page in light grey. It marks what a copy is for; it does not stop anyone from editing the PDF. Windows' Arial goes into the PDF with it, which adds about 1 MB to the file. The text is one line of up to 100 letters that Windows' Arial can draw: emoji, Chinese and invisible characters are refused with the reason.
  - **Print layout** > **2 pages per sheet** and **4 pages per sheet** put the pages on A4 sheets, to print on less paper.
  - **Fit pages to A4** scales Letter, A3 and odd-sized pages to A4. Pages already A4 stay as they are.
  - Print layout and Fit pages to A4 are made for printing: bookmarks, links and fillable fields are not kept, though filled-in answers and comments stay on the page.
  - **Lock filled forms** makes filled forms and comments part of the page, as the PDF draws them, so nobody can change the answers. Flevert draws an answer again only when the form asks for it and the answer is plain ASCII in a field of one line: a form that asks for it with other answers (Vietnamese ones, say, or in a field of several lines), or with an answer the PDF holds no drawing of, is left as it is, and the notification says why.
  - **Split by size** > **Under 10 MB** or **Under 25 MB** writes the pages, in order, into as few PDFs as fit under the limit of an email or a chat app.
  - **Extract images** saves the pictures of a PDF to a folder: JPEG photos exactly as they are in it, other pictures as PNG.
- Icons beside the entries of the Flevert menus (not inside their submenus), from Microsoft's Fluent UI System Icons, readable on the light and the dark menu, and separators between groups of entries.

### Changed

- The right-click menus are laid out for the jobs people do most, with plain names. Pictures: **Convert to**, **Compress**, **Remove hidden info**, **Combine into PDF**. PDFs: **Compress**, **Merge PDFs**, **Split into pages**, **Convert to**, **Rotate**, **Protect with password**, **Remove password**, **Remove hidden info**.
- New names: **Remove metadata** is **Remove hidden info**; **Convert** is **Convert to**, with WebP spelled that way; picture **Compress** levels are **Light (best quality)**, **Balanced** and **Strong (smallest)**; PDF **Compress** > Lossless and For sharing are **Smaller, same quality** and **Smallest, for sharing**; PDF **Convert** > JPG pages and PNG pages are **JPG images** and **PNG images**; **Rotate** > 90° right and 90° left are **Right 90°** and **Left 90°**. What each one does is unchanged.

### Known issues

- Installing an older version over 0.2.2 is not supported: uninstall first. The older setup does not know the **Flevert PDF tools** entry and leaves it in the menu, pointing to a list that is gone.

## [0.2.1] - 2026-10-20

### Added

- **Compress > For sharing** for PDFs: pictures above 150 DPI become 150 DPI JPEGs, so a scan often comes out at a quarter to a half of its size, depending on the scan, and still reads on a phone. Text, drawings and pictures already at 150 DPI or less stay exactly as they were.
- **Convert > Text** for PDFs: `report.txt` with the text of every page, a page break between pages, in UTF-8 that Notepad reads. A scan has no text to extract: Flevert does not recognise text in pictures.

### Changed

- **Split into pages** writes to a folder named `report (split)` instead of `report (pages)`, which is the name of the pictures from **Convert** > JPG pages and PNG pages.
- **Compress** for PDFs is now a submenu: **Lossless** (what **Compress** did) and **For sharing**.
- Every action that writes a PDF says when Flevert had to repair the PDF to read it, and asks you to check that the pages look right. A repaired result that does not open, or has a different number of pages than Flevert counted, fails instead. Pages lost by the repair itself cannot be detected, because Flevert counts the pages through the same repair. **Compress** > Lossless writes the repaired copy even when it is not smaller.
- **Remove metadata**, **Protect with password** and **Remove password** copy the PDF's streams as they are: content stored uncompressed is no longer compressed in the result.
- **Compress** > Lossless stops decompressing a PDF's streams after 10 seconds plus 1 second per MB. It then packs the PDF's objects but leaves its streams as they were, and the notification says the PDF was compressed only in part.
- **Convert** > JPG pages and PNG pages check, before the first page, that the pictures fit on the disk, and refuse PDFs whose pages together pass 5000 megapixels (2000 A4 pages at 150 DPI are 4350).
- **Compress**, **Remove metadata**, **Protect with password** and **Remove password** refuse PDFs of more than 2000 pages, as the other PDF actions do, and say when a signed PDF's signature is not valid in the result.

### Fixed

- A file of a **Merge PDFs** or **Combine into PDF** selection that started too slowly is reported as "did not reach Flevert in time", no longer as if its process had ended, and brings no second notification when it arrives at last. Files of a join you cancelled no longer bring a "left out" notification.
- A merge that would pass 2000 pages because a file changed while it ran says "Together these PDFs have more than 2000 pages".

### Security

- A tiny PDF that inflates to gigabytes no longer keeps **Remove metadata**, **Protect** or **Remove password** busy for seconds.
- Released files no longer contain the folder names of the machine that built them.

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

[Unreleased]: https://github.com/Suryvibegod/Flevert/compare/v1.2.3...HEAD
[1.2.3]: https://github.com/Suryvibegod/Flevert/compare/v1.2.2...v1.2.3
[1.2.2]: https://github.com/Suryvibegod/Flevert/compare/v0.2.1...v1.2.2
[0.2.1]: https://github.com/Suryvibegod/Flevert/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/Suryvibegod/Flevert/releases/tag/v0.2.0
