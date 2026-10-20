<p align="center"><img src="assets/icon-256.png" width="96" alt=""></p>

<h1 align="center">Flevert</h1>

<p align="center">
<b>Right-click a photo. Pick what you need. Done.</b><br>
HEIC to JPG, smaller files, a GPS-free copy to share: straight from Windows Explorer. Your photos never leave your PC.
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

- 📱 **iPhone photos that open everywhere**: HEIC to JPG, the right way up.
- 🪶 **Photos that fit**: shrink them for chat and email, or under 500 KB, 1 MB or 5 MB.
- 🔁 **Seven formats**: JPG, PNG, WebP, AVIF, GIF, BMP and TIFF.
- 📍 **Share without your location**: strip GPS and camera details first.
- 📄 **PDF tools**: photos and scans into one PDF, PDF pages to pictures, merge, split, rotate, compress, passwords.
- ⚡ **Up to 100 files at once**, with one notification at the end.
- 🛡️ **Originals untouched**: results land next to them, and nothing is overwritten.

## Install

1. Download `Flevert-Setup-<version>.exe` from the [latest release](https://github.com/Suryvibegod/Flevert/releases/latest).
2. Run it. It installs for your account only, so it needs no administrator rights.
3. Right-click a picture and choose **Flevert** (on Windows 11, under **Show more options** unless you picked the classic menu in setup).

Windows 10 (1809 or later) or 11, 64-bit. Flevert is not code-signed yet, so Windows says **Windows protected your PC**: click **More info**, then **Run anyway**. Before you do, you can [check your download](docs/install.md#check-your-download). No **Run anyway** button? See [Smart App Control](#smart-app-control).

## The menu

For `.jpg .jpeg .png .webp .heic .heif .avif .gif .bmp .tif .tiff`:

| Flevert > | You get |
|---|---|
| **Convert** > JPG, PNG, WEBP, AVIF, GIF, BMP, TIFF | `photo.jpg`, upright; animations stay animated in GIF and WebP |
| **Compress** > Light, Balanced, Strong | `photo (compressed).jpg`, usually in the same format |
| **Compress** > Under 500 KB, 1 MB, 5 MB | `photo (under 1 MB).jpg`, the best quality that fits |
| **Combine into PDF** | one `photo (+11).pdf` from all the selected pictures, in name order, the right way up, without GPS or camera details |
| **Remove metadata** | `photo (no metadata).jpg`, without GPS, camera details, XMP or comments |

HEIC and HEIF photos have no **Convert > GIF**: Explorer shows at most 16 entries in a menu like this one.

For `.pdf`:

| Flevert > | You get |
|---|---|
| **Convert** > JPG pages, PNG pages | a folder `report (pages)` with `report 001.jpg`, … |
| **Merge PDFs** | one `report (merged).pdf` from the selected PDFs, in name order |
| **Split into pages** | a folder `report (pages)` with one PDF per page |
| **Rotate** > 90° right, 90° left, 180° | `report (rotated).pdf` |
| **Compress** | `report (compressed).pdf`, smaller without losing quality |
| **Remove metadata** | `report (no metadata).pdf`, without author, dates, XMP, attachments or scripts |
| **Protect with password**, **Remove password** | `report (protected).pdf` (AES-256), `report (unlocked).pdf` |

PDFs are read in a separate, limited process, so a damaged or hostile file cannot take Flevert down with it. Batches longer than two seconds show a progress window with **Cancel**.

<p align="center">
<img src="assets/screenshots/progress.png" width="420" alt="The Flevert progress window converting twelve photos to JPG: 8 of 12 done, with a Cancel button">
</p>

## Privacy

No account, no telemetry, no upload. At most once a day, after a conversion, Flevert asks GitHub whether a newer version exists (GitHub sees your IP address and Flevert version) and, if so, offers to open its download page; nothing is downloaded or installed by itself. Turn it off in setup or with `flevert.exe update-check off`.

## Coming next

**0.2.1** adds more PDF tools: shrink scanned PDFs for sharing, reverse page order, repair, and extract text or pictures. **0.3** brings video and audio. See the [CHANGELOG](CHANGELOG.md), and share ideas in [issues](https://github.com/Suryvibegod/Flevert/issues).

## Troubleshooting

### Smart App Control

On Windows 11 with **Smart App Control** on, Windows may block setup, or parts of Flevert during setup or later, with no **Run anyway** button. **Keep it on**: turning it back on may mean resetting Windows. Signed releases should help ([watch the releases](https://github.com/Suryvibegod/Flevert/releases)); until then, if Flevert got installed, uninstall it ([by hand](docs/install.md#remove-flevert-by-hand) if Windows blocks that too). On a work or school PC, a policy against programs in the temporary folder can also stop setup's check: ask your IT department. If Smart App Control is off and setup still says Flevert stopped, [open an issue](https://github.com/Suryvibegod/Flevert/issues/new/choose) with its message.

### If the menu is missing

On Windows 11, look under **Show more options** (Shift+F10), and select at most 100 files. If setup said the classic-menu key already exists in another form, another program changed that setting and Flevert stays under **Show more options**. For any other problem setup reported, run it again; if it stays, [open an issue](https://github.com/Suryvibegod/Flevert/issues/new/choose) with its message. [Logs and more help](docs/install.md#if-the-menu-is-missing).

### Setup refused the install folder

Setup installs only into `%LOCALAPPDATA%\Programs\Flevert`, and only if no other Windows account can change it, because otherwise their code could run as you. If Flevert is already installed there (setup said it was installed anyway, or you were updating), do not use its uninstaller: [remove it by hand](docs/install.md#remove-flevert-by-hand). [Why it happens and how to fix it](docs/install.md#setup-refused-the-install-folder).

### Uninstall

**Settings > Apps > Installed apps > Flevert > Uninstall** removes the program, its menu, logs and settings, and the classic menu if setup turned it on. Your converted files stay. If Windows blocks the uninstaller, [remove Flevert by hand](docs/install.md#remove-flevert-by-hand).

## Contributing and license

Bug reports, ideas and pull requests are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md), and [SECURITY.md](SECURITY.md) for private security reports. Flevert is [MIT](LICENSE)-licensed; the licences of the bundled components are in `THIRD-PARTY-NOTICES.txt` in the install folder.
