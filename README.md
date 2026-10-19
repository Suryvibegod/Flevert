<p align="center"><img src="assets/icon-256.png" width="96" alt=""></p>

<h1 align="center">Flevert</h1>

<p align="center">
<b>Right-click a photo in Windows Explorer to convert it, shrink it or strip its location.</b><br>
Free, open source, offline. Your files never leave your computer.
</p>

<p align="center">
<a href="https://github.com/Suryvibegod/Flevert/releases/latest"><img alt="Download" src="https://img.shields.io/github/v/release/Suryvibegod/Flevert?label=download&color=0067c0"></a>
<a href="https://github.com/Suryvibegod/Flevert/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Suryvibegod/Flevert/actions/workflows/ci.yml/badge.svg"></a>
<img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4">
<a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

<p align="center">
<img src="assets/demo/convert.gif" width="720" alt="Four HEIC photos selected in Explorer; right-click, Flevert, Convert, JPG; four JPG copies appear beside them and a notification says Done: 4 files">
</p>

- **iPhone photos (HEIC) to JPG** in one click, turned the right way up.
- **Shrink photos** for chat apps or email, or to fit **under 500 KB, 1 MB or 5 MB**.
- **Convert** between JPG, PNG, WebP, AVIF, GIF, BMP and TIFF.
- **Remove location and camera details** (GPS, EXIF) before you share a photo.
- **Hundreds of files at once**, several in parallel, with one notification at the end.
- **Your originals are never touched**: results are saved next to them, and nothing is overwritten.

## Install

1. Download `Flevert-Setup-<version>.exe` from the [latest release](https://github.com/Suryvibegod/Flevert/releases/latest).
2. Run it. It installs for your account only, so it needs no administrator rights.
3. Right-click a picture and choose **Flevert**. On Windows 11 it is under **Show more options**, unless you picked the classic menu in setup.

Windows 10 (1809 or later) or 11, 64-bit. Nothing else to install.

> Flevert is not code-signed yet, so Windows shows **Windows protected your PC**: click **More info**, then **Run anyway**. [Check your download](docs/install.md#check-your-download) first if you want to be sure it is the real one.

## What it does

Works on `.jpg .jpeg .png .webp .heic .heif .avif .gif .bmp .tif .tiff`.

| Menu | You get |
|---|---|
| **Convert** > JPG, PNG, WEBP, AVIF, GIF, BMP, TIFF | `photo.jpg`, turned upright; animations stay animated in GIF and WebP |
| **Compress** > Light, Balanced, Strong | `photo (compressed).jpg`, usually in the same format |
| **Compress** > Under 500 KB, 1 MB, 5 MB | `photo (under 1 MB).jpg`, the best quality that fits |
| **Remove metadata** | `photo (no metadata).jpg`, without GPS, camera details, XMP or comments |

Colour profiles are kept. Batches that take more than two seconds show a progress window with **Cancel**.

<p align="center">
<img src="assets/screenshots/progress.png" width="420" alt="The Flevert progress window converting twelve photos to JPG: 8 of 12 done, with a Cancel button">
</p>

## Privacy

Everything happens on your computer: no account, no telemetry, no upload. The only network use is a check for a new version: after a conversion, at most once a day, Flevert asks GitHub for the newest version number and, if there is one, offers to open its download page. GitHub sees your IP address and your Flevert version, nothing about your files. Nothing is downloaded or installed by itself. Turn it off in setup or with `flevert.exe update-check off`.

## Roadmap

| Version | Adds |
|---|---|
| **0.1** | Pictures, the installer, update notices (this release) |
| **0.2** | PDF: photos and scans to one PDF, PDF to pictures, merge, split, rotate, compress, passwords |
| **0.3** | Video and audio: shrink videos for chat apps, convert MP4/MOV/MKV/WEBM, extract MP3, convert audio |

What changed: [CHANGELOG.md](CHANGELOG.md). Ideas and bug reports: [issues](https://github.com/Suryvibegod/Flevert/issues).

## Troubleshooting

### Smart App Control

On Windows 11 with **Smart App Control** on, Windows may block the unsigned setup or parts of Flevert, with no "Run anyway" button. **Please do not turn Smart App Control off for this**: it protects you from much more than this app, and turning it back on may require resetting Windows. Flevert should work on these PCs once its releases are signed; follow the [releases](https://github.com/Suryvibegod/Flevert/releases) to hear when. On a work or school PC, a security policy may also stop setup from running its check in the temporary folder; ask your IT department.

### If the menu is missing

- On Windows 11, look under **Show more options** (or press Shift+F10).
- Explorer shows the entry for up to **100** selected files; select fewer.
- If setup reported a problem with the menu, run it again. If the same problem comes back, [open an issue](https://github.com/Suryvibegod/Flevert/issues/new/choose) with its message.
- If setup could not switch to the classic menu, another program already changed that Windows setting; Flevert leaves it alone and stays under **Show more options**.
- Logs are in `%LOCALAPPDATA%\Flevert\logs` (kept 7 days). They contain file names and your Windows user name: replace anything private before posting them.

### Setup refused the install folder

Setup installs only into `%LOCALAPPDATA%\Programs\Flevert`, and only if no other Windows account can change it, because otherwise their code could run as you the next time you use the menu. It checks before copying anything, so nothing is left to clean up. [Why it happens and how to fix it](docs/install.md#setup-refused-the-install-folder).

### Uninstall

**Settings > Apps > Installed apps > Flevert > Uninstall** removes the program, its menu, logs and settings, and the classic menu if setup turned it on. Your converted files stay. If Windows blocks the uninstaller, [remove Flevert by hand](docs/install.md#remove-flevert-by-hand).

## Contributing

Bug reports, ideas and pull requests are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md) for how to build, test and propose changes, and [SECURITY.md](SECURITY.md) for reporting security problems privately.

## License

[MIT](LICENSE). Flevert ships the .NET runtime and Magick.NET (ImageMagick and the libraries it bundles); their licences are in `THIRD-PARTY-NOTICES.txt` in the install folder.
