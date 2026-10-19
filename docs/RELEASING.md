# Releasing ConvertAnything

For maintainers. Releases are built only by [`.github/workflows/release.yml`](../.github/workflows/release.yml), from a tag on `main`; nothing built on a personal machine is ever attached to a release ([ADR-0005](adr/0005-inno-setup-unsigned-until-signpath.md)).

## Before tagging

1. `<Version>` in `Directory.Build.props` is the new version, and `CHANGELOG.md` has its section.
2. CI is green on the commit three times in a row (re-run it from the Actions tab).
3. The three reviews (code, security, doubt) of every change since the last release found nothing Critical, High or Required.

## Tag and check the draft

1. `git tag -a vX.Y.Z -m "ConvertAnything X.Y.Z"` and `git push origin vX.Y.Z`. Only repository administrators can create `v*` tags (ruleset "Release tags").
2. The workflow tests, builds the installer, attests it and attaches it with `SHA256SUMS.txt` to a **draft** release.
3. Download the setup from the draft and check it as a user would:
   - `Get-FileHash` matches `SHA256SUMS.txt`.
   - `gh attestation verify <setup> --repo Suryvibegod/ConvertAnything --signer-workflow Suryvibegod/ConvertAnything/.github/workflows/release.yml --source-ref refs/tags/vX.Y.Z --deny-self-hosted-runners` passes.
   - `powershell -File scripts/test-installer.ps1 -Setup <setup>` passes on a machine without ConvertAnything.
   - The runtime in the installed `ca.deps.json` (`runtimepack.Microsoft.NETCore.App.Runtime.win-x64/...`) is the newest .NET 10 patch.
   - Microsoft Defender and VirusTotal report nothing. A detection by one or two engines with a generic name is usually a false positive of an unsigned Inno Setup installer: report it to the vendor, and do not publish until it is understood.
4. Publish the draft.

## If a published release is bad

Stop new downloads first, then fix forward. Users who already installed it keep working until they update or uninstall.

1. **Stop downloads:** edit the release on GitHub and turn it back into a draft (or, if it must stay visible, mark it as a pre-release and delete its setup asset). The README's download link then points at the previous release; for the very first release it shows no download until the fix is out.
2. **Tell users:** pin an issue (and a Discussions announcement) saying which version is affected, what goes wrong and what to do: usually "uninstall it from Settings > Apps", or the manual removal in the README if the uninstaller itself is the problem. For a security problem, publish a GitHub security advisory instead of an issue.
3. **Fix forward:** fix on `main` with a test that reproduces the problem, bump the patch version, add a CHANGELOG entry, and release it through the steps above. Do not move or reuse an existing tag: people and attestations refer to it.
4. If the release published something it must not (a secret, a private file), delete the release and its tag as an administrator, rotate the secret, and treat it as a security incident: downloads and attestations already made cannot be recalled.

Setup installs over an earlier version, so a fixed version reaches users by running the new setup; there is no automatic update in v0.1.
