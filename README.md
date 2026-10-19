# ConvertAnything

Right-click any image, video, audio or PDF file in Windows Explorer to convert or compress it in place. Everything runs offline on your machine; the result is saved next to the original file, which is never modified.

> **Status:** early development. The core (right-click menu, batching, progress, notifications) and the image operations work; media and PDF are being built. See [CAPABILITY-MAP.md](CAPABILITY-MAP.md).

## Image operations

For `.jpg .jpeg .png .webp .heic .heif .avif .gif .bmp .tif .tiff`:

| Menu | Result |
|---|---|
| **Convert > JPG / PNG / WEBP / AVIF / GIF / BMP / TIFF** | `photo.png` etc. Only formats other than the source are offered. Photos are turned upright using their EXIF orientation; transparent areas become white in JPG and BMP; animated GIF/WebP stay animated when converted to GIF or WebP. HEIC photos from iPhones open anywhere as JPG. |
| **Compress > Light / Balanced / Strong** | `photo (compressed).jpg`, same format where it compresses well (JPG, PNG, WebP, AVIF; PNG stays lossless except at Strong, which reduces it to 256 colours), otherwise JPG. Strong also shrinks the long edge to 2560 px. |
| **Compress > Under 500 KB / 1 MB / 5 MB** | `photo (under 1 MB).jpg`, the best quality that fits, shrinking the image only if it must. 1 MB means 1 000 000 bytes. |
| **Remove metadata** | `photo (no metadata).jpg` without camera details, GPS location, XMP, IPTC or comments. The colour profile is kept. |

Compress and Remove metadata keep the colour profile and drop all other metadata. Images are decoded by [Magick.NET](https://github.com/dlemstra/Magick.NET) with a strict allow-list of formats and resource limits ([ADR-0004](docs/adr/0004-magick-net-for-images.md)).

## How it works

- `ca.exe` registers a classic right-click menu entry, `ConvertAnything >`, under `HKCU`. No administrator rights are needed.
- Selecting several files and choosing one operation processes them as one batch, with a single notification at the end.
- Batches that take longer than two seconds show a progress window with **Cancel**.
- Output names never overwrite anything: `photo.png`, then `photo (1).png`, and so on. Partially written files are never left behind.
- Each processed file is logged to `%LOCALAPPDATA%\ConvertAnything\logs\ca-YYYYMMDD.log` (kept for 7 days).

## Limitations

- On Windows 11 the entry appears in the classic menu (**Show more options**, or Shift+F10), not the new compact menu.
- Explorer shows the entry for up to **100** selected files at once; select fewer files if it is missing.

## Build and test

Requires Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build ConvertAnything.sln
dotnet test ConvertAnything.sln
dotnet format ConvertAnything.sln --verify-no-changes
```

Publish a self-contained build:

```
dotnet publish src/ConvertAnything.Cli -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o artifacts/publish
```

## Try the core with the developer operations

The developer operations copy `.txt` files, which is enough to exercise the menu, batching and progress window.

```
artifacts\publish\ca.exe install-menu --dev --allow-insecure-location
```

`install-menu` refuses a folder that other accounts can modify, or one reached through a junction, because the menu runs `ca.exe` as whoever clicks it. A build or publish folder inside the repository usually fails that check; `--allow-insecure-location` accepts it on a machine you trust.

Right-click a `.txt` file, choose **Show more options**, then **ConvertAnything > Dev > Copy** (or **Slow copy**, which takes a second per file so you can see the progress window). Remove everything again with:

```
artifacts\publish\ca.exe uninstall-menu
```

`tools/MenuProbe` reads and invokes the real Explorer menu from the command line, which is how menu changes are verified without clicking:

```
dotnet run --project tools/MenuProbe -- list "C:\path\file.txt"
dotnet run --project tools/MenuProbe -- invoke "ConvertAnything/Dev/Copy" "C:\path\file.txt"
```

## Project documents

- [CAPABILITY-MAP.md](CAPABILITY-MAP.md): modules and build order
- `SPEC-<module>.md`: per-module specifications
- [docs/adr](docs/adr): architecture decisions
- [tasks/](tasks): current plan and task list

## License

[MIT](LICENSE)
