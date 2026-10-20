# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

Flevert has applied to the SignPath Foundation programme for open-source projects. Until the first signed release, releases are not code-signed; each release can still be checked with its SHA256 hash and the GitHub build attestation ([check your download](install.md#check-your-download)).

## What is signed

Only files built by this project from the source code in this repository:

- the installer, `Flevert-Setup-<version>.exe`;
- `flevert.exe` and the `Flevert.*.dll` assemblies inside it.

They are built only by the [release workflow](../.github/workflows/release.yml), on GitHub-hosted runners, from a version tag on `main`; nothing built on a personal computer is ever signed or attached to a release ([docs/RELEASING.md](RELEASING.md)). Signing happens in that workflow after the build and tests, and every release needs a manual approval before it is signed.

The installer also contains open-source components that Flevert does not build, such as pdfium, qpdf, ImageMagick and, from 1.3.0, FFmpeg. They are downloaded by the build from their projects' own releases, pinned by SHA256 ([scripts/deps.json](../scripts/deps.json)), and are not signed by Flevert. Their licences are in `THIRD-PARTY-NOTICES.txt` in the install folder.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [Suryvibegod](https://github.com/Suryvibegod) |
| Approvers (approve each signing request) | [Suryvibegod](https://github.com/Suryvibegod) (repository owner) |

Every member uses multi-factor authentication for GitHub and SignPath. Changes reach `main` only through the repository's protected-branch rules; the build scripts and workflows are reviewed like any other code.

## Privacy

Flevert converts files on your computer and does not send them anywhere. It has no account, no telemetry and no advertising.

The only network use is the check for a new version: after a conversion, at most once a day, Flevert asks GitHub for the number of the latest release (`api.github.com`), which lets GitHub see your IP address, your Flevert version and the time. Nothing about your files is sent, and nothing is downloaded or installed by itself. You can turn the check off in setup or with `flevert.exe update-check off` ([ADR-0006](adr/0006-update-check-against-github-releases.md)). GitHub's handling of that request is covered by the [GitHub Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

The bundled components (pdfium, qpdf, ImageMagick, FFmpeg) run only on files you choose and are not allowed to reach the network.
