# Contributing to Flevert

Thanks for helping! Bug reports, ideas, translations of the docs and code are all welcome.

## Reporting a bug

[Open an issue](https://github.com/Suryvibegod/Flevert/issues/new/choose) with:

- your Windows version and Flevert version (Settings > Apps > Installed apps);
- what you clicked and on which kind of file;
- the log line from `%LOCALAPPDATA%\Flevert\logs` if the notification mentioned one. Logs contain file names, folders and your Windows user name; replace anything private (for example with `<user>` or `photo.jpg`) before posting.

Please attach a sample file only if it contains nothing private. Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Suggesting a feature

Open an issue describing what you want to do with your files, not just the button you would add. The planned modules and what is out of scope are in [docs/specs/capability-map.md](docs/specs/capability-map.md).

## Working on the code

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). For the installer, also [Inno Setup](https://jrsoftware.org/isinfo.php) 6.7.3 or later (free; `winget install JRSoftware.InnoSetup --scope user`).

The PDF module needs pdfium and qpdf, which are downloaded (pinned by SHA256) into `deps/` rather than committed; the build stops with a message if they are missing.

```
powershell -ExecutionPolicy Bypass -File scripts/fetch-deps.ps1
dotnet build Flevert.sln
dotnet test tests/Flevert.Core.Tests
dotnet test tests/Flevert.Image.Tests
dotnet test tests/Flevert.Pdf.Tests
dotnet format Flevert.sln --verify-no-changes
powershell -File scripts/build-installer.ps1
```

Run the test projects one at a time, as above, not `dotnet test Flevert.sln`: run together, the image encodes starve the batching tests of CPU and those tests can fail.

To try the menu without the installer, register a development build. `install-menu` refuses a folder that other accounts can modify, or one reached through a junction, because the menu runs `flevert.exe` as whoever clicks it; `--allow-insecure-location` accepts it on a machine you trust, and `--dev` adds operations that copy `.txt` files to exercise batching and the progress window. Remove it again afterwards:

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

The main design decisions: the menu is a classic entry under `HKCU`, so no administrator rights ([ADR-0001](docs/adr/0001-registry-extendedsubcommandskey.md)); selecting many files starts one `flevert.exe` per file and they join into one batch over a named pipe ([ADR-0002](docs/adr/0002-batching-over-a-single-instance-pipe.md)); the app is a self-contained .NET 10 build ([ADR-0003](docs/adr/0003-self-contained-readytorun-winforms.md)); images go through Magick.NET with an allow-list of formats and resource limits ([ADR-0004](docs/adr/0004-magick-net-for-images.md)); PDFs are read by pdfium in a separate worker process and changed by qpdf, also run as its own process ([ADR-0008](docs/adr/0008-pdf-engines-pdfium-and-qpdf.md)). The rest are in [docs/adr](docs/adr). The README's demo animations are drawn by `assets/demo/make_demo_gif.py` (`python make_demo_gif.py convert convert.gif`, or `combine combine.gif`).

How the project works:

- Each module has a specification (`docs/specs/<module>.md`) and architecture decisions live in [docs/adr](docs/adr). Read the relevant ones before changing behaviour. Specs and plans are written in Vietnamese; code, UI, commits and the README in English.
- Write the test first and watch it fail, then make it pass. Bugs get a test that reproduces them.
- Keep pull requests small: one logical change, with a commit message that explains why.
- Anything that touches files, paths, the registry or decoding untrusted input gets extra care; the specs list the inputs Flevert refuses and why.
- After trying the right-click menu from a development build, remove it with `flevert.exe uninstall-menu`.

`scripts/test-installer.ps1` installs the real installer on your machine and uninstalls it again; it refuses to run if Flevert is already installed. How releases are made, checked and, if needed, withdrawn: [docs/RELEASING.md](docs/RELEASING.md).

## Code of conduct

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
