<p align="center"><img src="assets/icon-256.png" width="96" alt=""></p>

<h1 align="center">ConvertAnything</h1>

<p align="center">
Right-click a picture in Windows Explorer to convert it, compress it or strip its metadata.<br>
Free, open source and fully offline: your files never leave your computer.
</p>

<p align="center">
<a href="https://github.com/Suryvibegod/ConvertAnything/releases/latest"><img alt="Download" src="https://img.shields.io/github/v/release/Suryvibegod/ConvertAnything?label=download"></a>
<a href="https://github.com/Suryvibegod/ConvertAnything/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Suryvibegod/ConvertAnything/actions/workflows/ci.yml/badge.svg"></a>
<a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

- **iPhone photos (HEIC) to JPG** in one click, turned the right way up.
- **Compress photos** to send over Zalo, Messenger or email, or squeeze them **under 500 KB, 1 MB or 5 MB**.
- **Convert** between JPG, PNG, WebP, AVIF, GIF, BMP and TIFF.
- **Remove location and camera details** (EXIF, GPS) before you share a photo.
- Select many files at once: they are processed as one batch, with one notification at the end.
- The result is saved next to the original, which is never modified or overwritten.

Video, audio and PDF are next ([roadmap](#roadmap)).

## Install

1. Download `ConvertAnything-Setup-<version>.exe` from the [latest release](https://github.com/Suryvibegod/ConvertAnything/releases/latest).
2. Run it. It installs for your Windows account only, so it does not ask for administrator rights. Run it as yourself, not with "Run as administrator", or the menu ends up in the administrator's account.
3. Right-click a picture and choose **ConvertAnything**. On Windows 11 it is under **Show more options**, unless you chose the classic menu during setup.

Windows 10 (1809 or later) and Windows 11, 64-bit. Nothing else to install.

### Windows protected your PC (SmartScreen)

ConvertAnything is not code-signed yet, because a signing certificate costs money and this project is free (free signing for open-source projects has been requested). Until then Windows shows **Windows protected your PC** for a new download. Click **More info**, then **Run anyway**.

Only do that for the file from this repository's [Releases page](https://github.com/Suryvibegod/ConvertAnything/releases). To check that your download is exactly the file GitHub built from this source code, open PowerShell in your Downloads folder and compare:

```
Get-FileHash .\ConvertAnything-Setup-0.1.0.exe
```

with `SHA256SUMS.txt` on the release page. With the [GitHub CLI](https://cli.github.com/) you can also verify the build provenance: `gh attestation verify ConvertAnything-Setup-0.1.0.exe --repo Suryvibegod/ConvertAnything`.

### Smart App Control

On Windows 11 with **Smart App Control** turned on, Windows may block the unsigned setup or parts of ConvertAnything, and there is no "Run anyway" button. **Please do not turn Smart App Control off for this**: it protects you from much more than this app, and once off it cannot always be turned back on without resetting Windows. ConvertAnything will work on these PCs once its releases are signed. Follow the [releases](https://github.com/Suryvibegod/ConvertAnything/releases) to hear when.

### If the menu is missing

- On Windows 11, look under **Show more options** (or press Shift+F10).
- Explorer shows the entry for up to **100** selected files; select fewer.
- If setup reported a problem with the right-click menu, run setup again. If it reports the same problem, [open an issue](https://github.com/Suryvibegod/ConvertAnything/issues/new/choose) with the message it showed. Setup refuses to register the menu when other Windows accounts could change the files it runs, because they could then run programs as you.
- Logs of what ConvertAnything did are in `%LOCALAPPDATA%\ConvertAnything\logs` (kept for 7 days).

### Uninstall

**Settings > Apps > Installed apps > ConvertAnything > Uninstall** removes the program, its right-click menu, its logs and settings, and the classic menu if setup turned it on. Your converted files stay where they are. If a conversion is still running, uninstall asks you to wait until it has finished.

## What each command does

For `.jpg .jpeg .png .webp .heic .heif .avif .gif .bmp .tif .tiff`:

| Menu | Result |
|---|---|
| **Convert > JPG / PNG / WEBP / AVIF / GIF / BMP / TIFF** | `photo.png` etc. Only formats other than the source are offered. Photos are turned upright using their EXIF orientation; transparent areas become white in JPG and BMP; animated GIF/WebP stay animated when converted to GIF or WebP. HEIC photos from iPhones open anywhere as JPG. |
| **Compress > Light / Balanced / Strong** | `photo (compressed).jpg`, same format where it compresses well (JPG, PNG, WebP, AVIF; PNG stays lossless except at Strong, which reduces it to 256 colours), otherwise JPG. Strong also shrinks the long edge to 2560 px. |
| **Compress > Under 500 KB / 1 MB / 5 MB** | `photo (under 1 MB).jpg`, the best quality that fits, shrinking the image only if it must. 1 MB means 1 000 000 bytes. |
| **Remove metadata** | `photo (no metadata).jpg` without camera details, GPS location, XMP, IPTC or comments. The colour profile is kept. |

Compress and Remove metadata keep the colour profile and drop all other metadata. Output names never overwrite anything: `photo.png`, then `photo (1).png`, and so on. Batches that take longer than two seconds show a progress window with **Cancel**.

## Privacy

ConvertAnything works entirely on your computer. It has no account, no telemetry and does not connect to the internet.

## Roadmap

| Version | Adds |
|---|---|
| 0.1 | Images (this release) and the installer |
| 0.2 | Video and audio: compress videos to a size that fits messaging apps, convert MP4/MOV/MKV/WEBM, extract MP3, convert audio |
| 0.3 | PDF: to images, images to PDF, merge, split, compress |

Details in [CAPABILITY-MAP.md](CAPABILITY-MAP.md). Ideas and bug reports are welcome in [issues](https://github.com/Suryvibegod/ConvertAnything/issues).

## How it works

- `ca.exe` registers a classic right-click menu entry under `HKCU` (no administrator rights; [ADR-0001](docs/adr/0001-registry-extendedsubcommandskey.md)). Selecting several files starts one `ca.exe` per file; they join into one batch ([ADR-0002](docs/adr/0002-batching-over-a-single-instance-pipe.md)).
- Images are decoded by [Magick.NET](https://github.com/dlemstra/Magick.NET) with a strict allow-list of formats and resource limits ([ADR-0004](docs/adr/0004-magick-net-for-images.md)).
- The app is a self-contained .NET 10 build ([ADR-0003](docs/adr/0003-self-contained-readytorun-winforms.md)); the installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) ([ADR-0005](docs/adr/0005-inno-setup-unsigned-until-signpath.md)).

## Build from source

Requires Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); the installer also needs [Inno Setup 6.7.3](https://jrsoftware.org/isinfo.php) or later (free).

```
dotnet build ConvertAnything.sln
dotnet test ConvertAnything.sln
dotnet format ConvertAnything.sln --verify-no-changes
powershell -File scripts/build-installer.ps1     # artifacts\installer\ConvertAnything-Setup-<version>.exe
powershell -File scripts/test-installer.ps1      # installs it for real, checks, uninstalls
```

To try the menu without the installer, register a development build. `install-menu` refuses a folder that other accounts can modify, or one reached through a junction, because the menu runs `ca.exe` as whoever clicks it; `--allow-insecure-location` accepts it on a machine you trust, and `--dev` adds operations that copy `.txt` files to exercise batching and the progress window:

```
dotnet publish src/ConvertAnything.Cli -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o artifacts/publish
artifacts\publish\ca.exe install-menu --dev --allow-insecure-location
artifacts\publish\ca.exe uninstall-menu
```

`tools/MenuProbe` reads and invokes the real Explorer menu from the command line, which is how menu changes are verified without clicking:

```
dotnet run --project tools/MenuProbe -- list "C:\path\file.txt"
dotnet run --project tools/MenuProbe -- invoke "ConvertAnything/Dev/Copy" "C:\path\file.txt"
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for how the project works. Specifications are `SPEC-<module>.md`, decisions are in [docs/adr](docs/adr), plans in [tasks/](tasks).

## License

[MIT](LICENSE). ConvertAnything ships the .NET runtime and Magick.NET (ImageMagick and the libraries it bundles); their licences are in `THIRD-PARTY-NOTICES.txt` in the install folder.
