# ConvertAnything

Right-click any image, video, audio or PDF file in Windows Explorer to convert or compress it in place. Everything runs offline on your machine; the result is saved next to the original file, which is never modified.

> **Status:** early development. The core (right-click menu, batching, progress, notifications) works; file operations for images, media and PDF are being built. See [CAPABILITY-MAP.md](CAPABILITY-MAP.md).

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
artifacts\publish\ca.exe install-menu --dev
```

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
