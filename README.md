<p align="center"><img src="assets/icon-256.png" width="96" alt=""></p>

<h1 align="center">Flevert</h1>

<p align="center">
Right-click a picture in Windows Explorer to convert it, compress it or strip its metadata.<br>
Free, open source and fully offline: your files never leave your computer.
</p>

<p align="center">
<a href="https://github.com/Suryvibegod/Flevert/releases/latest"><img alt="Download" src="https://img.shields.io/github/v/release/Suryvibegod/Flevert?label=download"></a>
<a href="https://github.com/Suryvibegod/Flevert/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Suryvibegod/Flevert/actions/workflows/ci.yml/badge.svg"></a>
<a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

- **iPhone photos (HEIC) to JPG** in one click, turned the right way up.
- **Compress photos** to send over Zalo, Messenger or email, or squeeze them **under 500 KB, 1 MB or 5 MB**.
- **Convert** between JPG, PNG, WebP, AVIF, GIF, BMP and TIFF.
- **Remove location and camera details** (EXIF, GPS) before you share a photo.
- Select many files at once: they are processed as one batch, several at a time on PCs with more than two cores, with one notification at the end.
- The result is saved next to the original, which is never modified or overwritten.

<p align="center">
<img src="assets/screenshots/progress.png" width="420" alt="The Flevert progress window converting twelve photos to JPG: 8 of 12 done, with a Cancel button">
</p>

Video, audio and PDF are next ([roadmap](#roadmap)).

## Install

1. Download `Flevert-Setup-<version>.exe` from the [latest release](https://github.com/Suryvibegod/Flevert/releases/latest).
2. Run it. It installs for your Windows account only, so it does not ask for administrator rights. Run it as yourself, not with "Run as administrator", or the menu ends up in the administrator's account.
3. Right-click a picture and choose **Flevert**. On Windows 11 it is under **Show more options**, unless you chose the classic menu during setup.

Windows 10 (1809 or later) and Windows 11, 64-bit. Nothing else to install.

### Windows protected your PC (SmartScreen)

Flevert is not code-signed yet, because a signing certificate costs money and this project is free (free signing for open-source projects has been requested). Until then Windows shows **Windows protected your PC** for a new download, with **More info** and then **Run anyway**.

**Before** you click **Run anyway**, make sure the file came from this repository's [Releases page](https://github.com/Suryvibegod/Flevert/releases) and is intact:

1. Open PowerShell in your Downloads folder and run `Get-FileHash .\Flevert-Setup-0.1.0.exe`. The hash must match the line in `SHA256SUMS.txt` on the release page (upper or lower case does not matter). This shows the download is complete and unchanged.
2. To check that GitHub built it from this repository's source code, verify its build provenance with the [GitHub CLI](https://cli.github.com/). It needs a free GitHub account: run `gh auth login` once first.

   ```
   gh attestation verify Flevert-Setup-0.1.0.exe --repo Suryvibegod/Flevert --signer-workflow Suryvibegod/Flevert/.github/workflows/release.yml --source-ref refs/tags/v0.1.0 --deny-self-hosted-runners
   ```

A matching hash alone does not prove where the file came from: anyone who replaced the file could replace the hash beside it. The provenance check shows that this repository's release workflow built the file from the tag `v0.1.0` on GitHub's own machines.

### Smart App Control

On Windows 11 with **Smart App Control** turned on, Windows may block the unsigned setup or parts of Flevert, and there is no "Run anyway" button. **Please do not turn Smart App Control off for this**: it protects you from much more than this app, and turning it back on may require resetting Windows. Flevert is more likely to work on these PCs once its releases are signed. Follow the [releases](https://github.com/Suryvibegod/Flevert/releases) to hear when.

On a work or school PC, a security policy (AppLocker, or security software) may stop programs from running in the temporary folder, where setup checks the install folder before copying anything. Setup then stops without installing; ask your IT department.

### If the menu is missing

- On Windows 11, look under **Show more options** (or press Shift+F10).
- Explorer shows the entry for up to **100** selected files; select fewer.
- If setup reported a problem with the right-click menu, run setup again. If it reports the same problem, [open an issue](https://github.com/Suryvibegod/Flevert/issues/new/choose) with the message it showed.
- If setup said it could not switch to the classic menu, another program has already changed that Windows setting in a way Flevert does not recognise, so it leaves it alone. Flevert is still under **Show more options**.
- Logs of what Flevert did are in `%LOCALAPPDATA%\Flevert\logs` (kept for 7 days). They contain your file names and your Windows user name: replace anything private before you post them.

### Setup refused the install folder

Flevert always installs into `%LOCALAPPDATA%\Programs\Flevert`, and only if no other Windows account can change that folder, any folder above it, or the files in it, and none of them is a junction or symbolic link to somewhere else. Otherwise another account could replace the program, and the next time you used the menu, their code would run as you. Setup checks this before it copies anything, so a refused setup leaves nothing to uninstall. Running setup again does not change the result.

It usually happens when:

- your `AppData` folder was moved to another drive (for example with a junction), or lives on a drive whose permissions let every user change files, as many second drives do;
- someone changed the permissions of `%LOCALAPPDATA%\Programs` or of the folder itself;
- `%LOCALAPPDATA%\Programs\Flevert` already holds files from something else.

Put the folder back where Windows keeps it with its default permissions (or delete `%LOCALAPPDATA%\Programs\Flevert` if it holds leftover files), then run setup again. If Flevert is already installed in a folder that setup now refuses (for example when updating), do not use its uninstaller and do not just repair the permissions: another account may have changed the files there. Remove it [by hand](#uninstall), then run setup again. If you are not sure what changed, [open an issue](https://github.com/Suryvibegod/Flevert/issues/new/choose) with the message setup showed.

### Uninstall

**Settings > Apps > Installed apps > Flevert > Uninstall** removes the program, its right-click menu, its logs and settings, and the classic menu if setup turned it on. Your converted files stay where they are. If a conversion is still running, uninstall asks you to wait until it has finished.

If Windows blocks the uninstaller (Smart App Control can), remove Flevert by hand. These PowerShell commands run nothing of Flevert's; they only delete what it created:

```
# The right-click menu
Get-ChildItem HKCU:\Software\Classes\SystemFileAssociations -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath "$($_.PSPath)\shell\Flevert" -Recurse -ErrorAction SilentlyContinue }
Get-ChildItem HKCU:\Software\Classes | Where-Object PSChildName -like 'Flevert.Menu*' | Remove-Item -Recurse
# The classic menu, only if setup turned it on (it marks the key; a key you created yourself is left alone)
$k = 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}'
if (Get-ItemProperty -LiteralPath $k -Name Flevert -ErrorAction SilentlyContinue) { Remove-Item -LiteralPath $k -Recurse }
# The entry in Installed apps
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B7A3F52-2C64-4F0B-9E3D-5A1C7E2B9D40}_is1' -ErrorAction SilentlyContinue
# The program, its logs and settings (rmdir removes a junction itself, never what it points to)
cmd /d /c 'rmdir /s /q "%LOCALAPPDATA%\Programs\Flevert" 2>nul'
cmd /d /c 'for %d in (logs pipes magick) do @rmdir /s /q "%LOCALAPPDATA%\Flevert\%d" 2>nul'
cmd /d /c 'rmdir "%LOCALAPPDATA%\Flevert" 2>nul'
if (Test-Path -LiteralPath "$env:LOCALAPPDATA\Programs\Flevert") { 'Not fully removed: wait for any conversion to finish, then run these lines again.' }
```

Then sign out and back in.

## What each command does

For `.jpg .jpeg .png .webp .heic .heif .avif .gif .bmp .tif .tiff`:

| Menu | Result |
|---|---|
| **Convert > JPG / PNG / WEBP / AVIF / GIF / BMP / TIFF** | `photo.png` etc. Only formats other than the source are offered. Photos are turned upright using their EXIF orientation; transparent areas become white in JPG and BMP; animated GIF/WebP stay animated when converted to GIF or WebP. HEIC photos from iPhones open anywhere as JPG. |
| **Compress > Light / Balanced / Strong** | `photo (compressed).jpg`, same format where it compresses well (JPG, PNG, WebP, AVIF; PNG stays lossless except at Strong, which reduces it to 256 colours), otherwise JPG; animated GIFs stay GIF. Strong also shrinks JPG, WebP and AVIF results to 2560 px on the long edge. |
| **Compress > Under 500 KB / 1 MB / 5 MB** | `photo (under 1 MB).jpg`, the best quality that fits, shrinking the image when it has to. 1 MB means 1 000 000 bytes. |
| **Remove metadata** | `photo (no metadata).jpg` without camera details, GPS location, XMP, IPTC or comments. The colour profile is kept. |

Compress and Remove metadata keep the colour profile and drop all other metadata. Output names never overwrite anything: `photo.png`, then `photo (1).png`, and so on. Batches that take longer than two seconds show a progress window with **Cancel**.

## Privacy

Flevert works entirely on your computer. It has no account, no telemetry and does not connect to the internet.

## Roadmap

| Version | Adds |
|---|---|
| 0.1 | Images (this release) and the installer |
| 0.2 | Video and audio: compress videos to a size that fits messaging apps, convert MP4/MOV/MKV/WEBM, extract MP3, convert audio |
| 0.3 | PDF: to images, images to PDF, merge, split, compress |

What changed in each release: [CHANGELOG.md](CHANGELOG.md). Details of what is planned: [docs/specs/capability-map.md](docs/specs/capability-map.md). Ideas and bug reports are welcome in [issues](https://github.com/Suryvibegod/Flevert/issues).

## How it works

- `flevert.exe` registers a classic right-click menu entry under `HKCU` (no administrator rights; [ADR-0001](docs/adr/0001-registry-extendedsubcommandskey.md)). Selecting several files starts one `flevert.exe` per file; they join into one batch ([ADR-0002](docs/adr/0002-batching-over-a-single-instance-pipe.md)).
- Images are decoded by [Magick.NET](https://github.com/dlemstra/Magick.NET) with a strict allow-list of formats and resource limits ([ADR-0004](docs/adr/0004-magick-net-for-images.md)).
- The app is a self-contained .NET 10 build ([ADR-0003](docs/adr/0003-self-contained-readytorun-winforms.md)); the installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) ([ADR-0005](docs/adr/0005-inno-setup-unsigned-until-signpath.md)).

## Build from source

Requires Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); the installer also needs [Inno Setup 6.7.3](https://jrsoftware.org/isinfo.php) or later (free).

```
dotnet build Flevert.sln
dotnet test Flevert.sln
dotnet format Flevert.sln --verify-no-changes
powershell -File scripts/build-installer.ps1     # artifacts\installer\Flevert-Setup-<version>.exe
powershell -File scripts/test-installer.ps1      # installs it for real, checks, uninstalls
```

To try the menu without the installer, register a development build. `install-menu` refuses a folder that other accounts can modify, or one reached through a junction, because the menu runs `flevert.exe` as whoever clicks it; `--allow-insecure-location` accepts it on a machine you trust, and `--dev` adds operations that copy `.txt` files to exercise batching and the progress window:

```
dotnet publish src/Flevert.Cli -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o artifacts/publish
artifacts\publish\flevert.exe install-menu --dev --allow-insecure-location
artifacts\publish\flevert.exe uninstall-menu
```

`tools/MenuProbe` reads and invokes the real Explorer menu from the command line, which is how menu changes are verified without clicking:

```
dotnet run --project tools/MenuProbe -- list "C:\path\file.txt"
dotnet run --project tools/MenuProbe -- invoke "Flevert/Dev/Copy" "C:\path\file.txt"
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for how the project works. Specifications are `docs/specs/<module>.md`, decisions are in [docs/adr](docs/adr), plans in [tasks/](tasks).

## License

[MIT](LICENSE). Flevert ships the .NET runtime and Magick.NET (ImageMagick and the libraries it bundles); their licences are in `THIRD-PARTY-NOTICES.txt` in the install folder.
