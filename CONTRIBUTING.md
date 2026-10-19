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

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). For the installer, also [Inno Setup 6](https://jrsoftware.org/isinfo.php) (free; `winget install JRSoftware.InnoSetup --scope user`).

```
dotnet build Flevert.sln
dotnet test tests/Flevert.Core.Tests
dotnet test tests/Flevert.Image.Tests
dotnet format Flevert.sln --verify-no-changes
powershell -File scripts/build-installer.ps1
```

How the project works:

- Each module has a specification (`docs/specs/<module>.md`) and architecture decisions live in [docs/adr](docs/adr). Read the relevant ones before changing behaviour. Specs and plans are written in Vietnamese; code, UI, commits and the README in English.
- Write the test first and watch it fail, then make it pass. Bugs get a test that reproduces them.
- Keep pull requests small: one logical change, with a commit message that explains why.
- Anything that touches files, paths, the registry or decoding untrusted input gets extra care; the specs list the inputs Flevert refuses and why.
- After trying the right-click menu from a development build, remove it with `flevert.exe uninstall-menu`.

`scripts/test-installer.ps1` installs the real installer on your machine and uninstalls it again; it refuses to run if Flevert is already installed. How releases are made, checked and, if needed, withdrawn: [docs/RELEASING.md](docs/RELEASING.md).

## Code of conduct

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
