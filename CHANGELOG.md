# Changelog

All notable changes to ConvertAnything are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-10-19

The first release: pictures, and an installer.

### Added

- Right-click any `.jpg .jpeg .png .webp .heic .heif .avif .gif .bmp .tif .tiff` file in Windows Explorer and choose **ConvertAnything**:
  - **Convert** to JPG, PNG, WebP, AVIF, GIF, BMP or TIFF. iPhone photos (HEIC) open anywhere as JPG, turned the right way up; animations stay animated in GIF and WebP.
  - **Compress** at three levels, or to fit **under 500 KB, 1 MB or 5 MB**.
  - **Remove metadata**: camera details, GPS location, XMP, IPTC and comments, keeping the colour profile.
- Select many files at once: they are converted several at a time as one batch, with one notification at the end. Batches that take more than two seconds show a progress window with **Cancel**.
- Results are saved next to the original, which is never changed or overwritten; names never clash (`photo (1).jpg`).
- A per-user installer that needs no administrator rights, with an option to bring back the classic Windows 10 style right-click menu on Windows 11. Uninstalling removes everything ConvertAnything created.
- Works entirely offline: no account, no telemetry, no network access.

### Security

- Images are decoded by ImageMagick with an allow-list of formats, resource limits and a fixed policy that ConvertAnything checks before every run.
- Setup installs only into a folder no other Windows account can change, and checks it before copying anything.
- Releases are built by GitHub Actions with a build provenance attestation and a `SHA256SUMS.txt` (see the README for how to verify a download). They are not code-signed yet.

[0.1.0]: https://github.com/Suryvibegod/ConvertAnything/releases/tag/v0.1.0
