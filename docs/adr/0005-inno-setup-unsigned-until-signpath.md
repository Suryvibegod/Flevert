# ADR-0005: Ship a per-user Inno Setup installer, unsigned until free OSS signing is granted

## Status

Accepted

## Date

2026-10-19

## Context

v0.1 needs an installer that a non-technical user can download and run. The maintainer requires everything to be free: build tools, packaging and code signing. The right-click menu lives in HKCU (ADR-0001), so nothing needs administrator rights, and `ca.exe install-menu` refuses a folder other accounts can change (`InstallLocation`).

On the development machine (Windows 11 with Smart App Control enforcing), freshly built unsigned files were blocked at random, by hash: test DLLs, `MenuProbe.exe`, and two of eleven installer builds. Six builds with Inno's `windows11` wizard style and three without all ran when retried, so the style is not the cause.

## Decision

- **Inno Setup 6.7.3** builds `ConvertAnything-Setup-<version>.exe`. Its licence allows any use without a fee (commercial users are asked, not required, to buy a licence). It installs per user without elevation (`PrivilegesRequired=lowest`) into `%LOCALAPPDATA%\Programs\ConvertAnything`, which `PrepareToInstall` enforces because `/DIR` would otherwise override the hidden folder page.
- `PrepareToInstall` also refuses, before copying anything, a folder that is or lies under a junction or symbolic link (files would land wherever it points), and a folder that already holds files when no uninstall entry of this app exists (`ca.exe` would load a DLL placed there beforehand). Silent setup exits with Inno's code 7 in all three cases.
- All registry logic stays in `ca.exe` (testable C#). Setup runs `install-menu` and `classic-menu on|off` and reports the first line of their stderr (the log gets every line); uninstall runs `uninstall-menu` and `classic-menu off`, then a Pascal sweep removes the same keys in case `ca.exe` cannot run. Running `ca.exe` is wrapped so that an exception cannot skip the sweep. `InstallerScriptTests` keeps the names in step.
- Setup exits with **10** when it installed the files but could not register the menu or switch on the classic menu, so silent installs can detect it. The classic menu is not switched on when the menu itself failed. `ca.exe` exit code 3 (folder refused) gets its own message and README section, because running setup again cannot fix it; codes outside 1–3 say "Windows blocked ConvertAnything or it stopped unexpectedly".
- Running `ca.exe` holds the `ConvertAnything.App` mutex until the process ends. With `AppMutex`, setup and uninstall do not wait on their own: they ask the user to wait for the conversion and click OK, or Cancel; silent runs cancel. This is deliberate, because a per-user uninstaller cannot remove files in use and nothing would be left to remove them later. `CloseApplications` only matters for a `ca.exe` that could not open the mutex.
- **Releases are not code-signed in v0.1.** Paid options (OV/EV certificates, Azure Trusted Signing) are ruled out; a self-signed certificate does nothing for SmartScreen or Smart App Control. Once the repository is public, the maintainer applies to the **SignPath Foundation**, which signs open-source releases for free from a CI build.
- Releases are built only by `.github/workflows/release.yml`, only from tags on `main`: the build job (tests, vulnerable-package check, Inno Setup from its official release with a hash check, the compiler called by its path) has read-only rights; a separate job with only the attestation rights adds a GitHub build provenance attestation; another with write rights only attaches the files and `SHA256SUMS.txt` to a **draft** release. CI and release use the newest .NET 10 patch (`10.0.x`), because a self-contained release ships that runtime; Dependabot proposes updates to the pinned actions and to NuGet packages.
- Before publishing a release, the maintainer uploads the installer to VirusTotal: new unsigned Inno Setup installers are often flagged by heuristics, and false positives can be reported to Microsoft and the vendors.

## Consequences

- Users see SmartScreen's **Windows protected your PC** for a new download; the README explains **More info > Run anyway**, how to check the hash and the provenance, and says plainly not to turn Smart App Control off. On PCs with Smart App Control enforcing, v0.1 may not run at all.
- SignPath approval is not guaranteed (it looks at the project's maturity, its CI and its signing policy) and may take time. If it is declined, the project stays unsigned and documents that; Certum's low-cost open-source certificate would be the fallback only if the maintainer ever accepts a cost.
- Signing covers only the project's own files. The Magick.NET managed DLLs are unsigned upstream, and the ffmpeg, qpdf and pdfium binaries planned for v0.2 and v0.3 usually are too, so Smart App Control may still block parts of a signed release. Signed builds also start without SmartScreen reputation, which grows with downloads.
- The installer ships `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`, collected from every package in `ca.deps.json`; the build fails if a package has no licence file.
- Trade-offs kept: `.claude/settings.json` stays in the repository, because it enables the review process the maintainer requires, and Claude Code asks before trusting a folder anyway. Tests that measure time also run on CI runners, which are slower and busier; they are widened only if they fail there.
- Not covered yet: upgrading from an older version with different files (no older version exists before v0.1; a v0.1 setup will be kept as a test fixture for v0.2), and ARM64 Windows, which runs the x64 build through emulation but has not been tested.

## Alternatives considered

- **WiX 6 / MSI:** free for open-source projects, but its Open Source Maintenance Fee terms for organisations are less clear, and MSI is heavier for a per-user install.
- **MSIX:** cannot be installed without a trusted signature.
- **A zip with `ca.exe install-menu`:** no entry in Installed apps, and users tend to run it from Downloads and delete it, leaving a menu that points nowhere.
