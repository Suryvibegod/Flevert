# Contributing to ConvertAnything

Thanks for helping! Bug reports, ideas, translations of the docs and code are all welcome.

## Reporting a bug

[Open an issue](https://github.com/Suryvibegod/ConvertAnything/issues/new/choose) with:

- your Windows version and ConvertAnything version (Settings > Apps > Installed apps);
- what you clicked and on which kind of file;
- the log line from `%LOCALAPPDATA%\ConvertAnything\logs` if the notification mentioned one.

Please attach a sample file only if it contains nothing private. Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Suggesting a feature

Open an issue describing what you want to do with your files, not just the button you would add. The planned modules and what is out of scope are in [CAPABILITY-MAP.md](CAPABILITY-MAP.md).

## Working on the code

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). For the installer, also [Inno Setup 6](https://jrsoftware.org/isinfo.php) (free; `winget install JRSoftware.InnoSetup --scope user`).

```
dotnet build ConvertAnything.sln
dotnet test ConvertAnything.sln
dotnet format ConvertAnything.sln --verify-no-changes
powershell -File scripts/build-installer.ps1
```

How the project works:

- Each module has a specification (`SPEC-<module>.md`) and architecture decisions live in [docs/adr](docs/adr). Read the relevant ones before changing behaviour. Specs and plans are written in Vietnamese; code, UI, commits and the README in English.
- Write the test first and watch it fail, then make it pass. Bugs get a test that reproduces them.
- Keep pull requests small: one logical change, with a commit message that explains why.
- Anything that touches files, paths, the registry or decoding untrusted input gets extra care; the specs list the inputs ConvertAnything refuses and why.
- After trying the right-click menu from a development build, remove it with `ca.exe uninstall-menu`.

`scripts/test-installer.ps1` installs the real installer on your machine and uninstalls it again; it refuses to run if ConvertAnything is already installed.

## Code of conduct

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
