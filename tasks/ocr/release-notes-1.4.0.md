# Release notes 1.4.0 (bản nháp)

Ghi chú cho phiên phát hành (tiếng Việt, xóa trước khi dán):

- `release.yml` tự viết phần đầu của release notes từ mục `## [1.4.0]` của CHANGELOG, rồi thêm "Check your download" (lệnh hash và `gh attestation verify` ghim commit, cho setup và cho tarball Linux) và "Source of the bundled ffmpeg". Bản nháp dưới đây là phần đầu đầy đủ hơn; khi draft đã có, so với notes workflow viết, điền chỗ trống rồi `gh release edit v1.4.0 --notes-file <file>` **trước khi publish** (sau publish release bất biến).
- Điền: ngày trong CHANGELOG (`YYYY-MM-DD`), kích thước và SHA256 của `Flevert-Setup-1.4.0.exe` (từ `SHA256SUMS.txt` của draft, kiểm lại bằng `Get-FileHash`), kích thước và SHA256 của tarball Linux.
- Tarball Linux: nhánh `ci/release-linux` thêm vào `release.yml` job `build-linux` (kiểm tag, test Core/Image/Pdf/Python, `scripts/build-linux.sh`, kiểm cài/cập nhật/gỡ, kiểm đường dẫn máy build), job `attest-linux`, và job `release` ghi dòng của tarball vào `SHA256SUMS.txt` rồi đính kèm. Cần merge vào `main` trước khi tag. Lần chạy tag đầu tiên là lần đầu job này chạy thật (workflow chỉ chạy theo tag): theo dõi job `build-linux` và kiểm tarball trên draft theo bước 4 của "Tag and check the draft" trong docs/RELEASING.md.
- Kích thước: với bản tesseract cũ (`deps-tesseract-5.5.3`, zip 3,3 MB, có libcurl/libarchive) setup là 100,2 MB (1.3.0: 97,7 MB). Bản đang ghim là `deps-tesseract-5.5.3-2` (UTF-8, không mã mạng, zip 2,6 MB; dựng từ main 1577b28, run 40121734076, attestation `--source-ref refs/heads/main` đã kiểm đạt 2026-10-25), nên setup nhỏ hơn một chút: lấy số thật từ draft.

---

Text from pictures and scans, in Vietnamese and English, read on your PC. And Flevert now runs on Linux too, in the right-click menu of GNOME Files.

## What's new

### Text from pictures and scans (Windows)

- **Pictures:** right-click a screenshot or a photo of a page (JPG, PNG, HEIC, WebP, AVIF, GIF, BMP, TIFF) and choose **Flevert > Convert to > Text**. You get `photo.txt` beside it, in UTF-8 that Notepad opens. A photo turned sideways or upside down is read the right way up when it has enough text for Tesseract to be sure which way is up.
- **Scanned PDFs you can search:** hold **Shift**, right-click a PDF and choose **Flevert: more PDF tools > Make searchable (OCR)**. `report (searchable).pdf` has the same pages with invisible text over the scanned ones, so Ctrl+F finds words and you can copy them with their Vietnamese accents. A page scanned sideways or upside down is turned the right way up, without re-encoding it; every other page looks exactly as it did.
- **PDF to text reads scans:** **Convert to > Text** of a PDF now reads its scanned pages from their pictures, where 1.3.0 said the PDF had no text. If Tesseract cannot read one page, that page keeps the text the PDF itself had, and the notification names it.
- Everything is read on your PC: no file leaves it, and nothing is downloaded.

### Flevert for Linux

The picture and PDF tools of Flevert, in the right-click menu of GNOME Files (Nautilus), with the same names, the same result files and the same notifications as on Windows. Download `Flevert-1.4.0-linux-x64.tar.gz`, unpack it and run `./install.sh`: no root needed (Files needs its `nautilus-python` package, and the script says how to install it). The PDF tools that Windows shows only with Shift are under **Flevert > More PDF tools**. For 64-bit x86 Linux with GNOME Files and glibc 2.34 or later (Ubuntu 22.04, Debian 12, Fedora 35 and later). Video and audio, and text from pictures and scans, are on Windows only for now. Thanks to [tlong (@long89kev)](https://github.com/long89kev), who wrote the Linux version ([PR #5](https://github.com/Suryvibegod/Flevert/pull/5)).

## What changed

- **Convert to > BMP** left the picture menu to make room for **Text** (Explorer shows at most 16 entries in one menu). BMP pictures still open in every tool: convert them to PNG for a lossless copy.

## Known limits

- Text from pictures and scans is on Windows only.
- A scanned page that already has more than 10 characters of real text on it (a typed header, a "Scanned with …" line, Bates numbers, a page stamp) counts as a text page and is not read by OCR, in **Convert to > Text** or **Make searchable (OCR)**.
- Turned pages and photos are found only when they have enough text, about ten lines; one with a line or two is read as it is.
- Pictures larger than 25 megapixels are shrunk to that size first, so very small print in a huge photo may be missed. Tesseract gets 150 seconds and 1 GB of memory for each picture or page.
- Only Vietnamese and English are read.
- Flevert is still not code-signed, so Windows shows **Windows protected your PC**: check the download as below, then **More info > Run anyway**.

## Download

| File | Size | SHA256 |
|---|---|---|
| `Flevert-Setup-1.4.0.exe` (Windows 10 1809 and later, 11; 64-bit) | ___ MB (1.3.0: 97.7 MB) | `________________________________________________________________` |
| `Flevert-1.4.0-linux-x64.tar.gz` | ___ MB | `________________________________________________________________` |

The setup is a few MB larger than 1.3.0's, for Tesseract and its Vietnamese, English and page-orientation data. Setup installs over 1.3.0 and keeps your choices (classic menu, update check).

## Licences

- Text is read by [Tesseract](https://github.com/tesseract-ocr/tesseract) 5.5.3 (Apache-2.0), which Flevert builds from source on GitHub Actions ([build-tesseract workflow](https://github.com/Suryvibegod/Flevert/actions/workflows/build-tesseract.yml)) as one program with only permissive libraries linked in (Leptonica, libpng, libspng, libjpeg-turbo, libwebp, OpenJPEG, libtiff, giflib, zlib and liblzma, under BSD, MIT, zlib-style and 0BSD licences), no GPL or LGPL code and no network code. The build is attested and pinned by SHA256. It runs as a separate program with a memory limit, is given only pictures Flevert wrote itself, and works in a Windows account whose name has Vietnamese letters.
- The trained data is `tessdata_fast` 4.1.0 (`vie`, `eng`, `osd`, Apache-2.0).
- Every licence is in `THIRD-PARTY-NOTICES.txt` in the install folder. ffmpeg is still GPL and its complete source is attached below, as for 1.3.0.
