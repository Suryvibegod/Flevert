# Releasing Flevert

For maintainers. Releases are built only by [`.github/workflows/release.yml`](../.github/workflows/release.yml), from a tag on `main`; nothing built on a personal machine is ever attached to a release ([ADR-0005](adr/0005-inno-setup-unsigned-until-signpath.md)).

## Before tagging

1. `<Version>` in `Directory.Build.props` is the new version, and `CHANGELOG.md` has its section.
2. CI is green on the commit three times in a row (re-run it from the Actions tab).
3. The three reviews (code, security, doubt) of every change since the last release found nothing Critical, High or Required.

## Tag and check the draft

1. `git tag -a vX.Y.Z -m "Flevert X.Y.Z"` and `git push origin vX.Y.Z`. Only repository administrators can create `v*` tags (ruleset "Release tags").
2. The workflow tests, builds the installer, attests it and attaches it with `SHA256SUMS.txt` to a **draft** release. The release notes are the version's CHANGELOG section plus how to check the download; the workflow fails if the CHANGELOG has no section for the version.
3. Download the setup from the draft and check it as a user would:
   - `Get-FileHash` matches `SHA256SUMS.txt`.
   - The `gh attestation verify ...` command in the release notes passes. It pins the tag and the commit (`--source-digest`).
   - `powershell -File scripts/test-installer.ps1 -Setup <setup>` passes on a machine without Flevert.
   - The runtime in the installed `flevert.deps.json` (`runtimepack.Microsoft.NETCore.App.Runtime.win-x64/...`) is the newest .NET 10 patch.
   - Microsoft Defender and VirusTotal report nothing. A detection by one or two engines with a generic name is usually a false positive of an unsigned Inno Setup installer: report it to the vendor, and do not publish until it is understood.
4. Publish the draft.

Until it is published, a draft may be thrown away and rebuilt: `gh release delete vX.Y.Z --cleanup-tag --yes`, then tag again. Delete the old run's artifacts too, so only one build of the version can pass the provenance check. Once published, the release is immutable (repository setting "Immutable releases"): its assets and tag cannot change, and the tag name can never be used again, even after the release is deleted.

## If a published release is bad

Stop new downloads first, then fix forward. Users who already installed it keep working until they update or uninstall.

1. **Stop downloads:** the assets of a published release cannot be removed (immutable releases), so edit the release: mark it as a pre-release, which takes it off "latest", and put a warning at the top of its notes. If it must disappear, delete the release; its tag name stays burnt. For the very first release the README's download link then shows no download until the fix is out.
2. **Tell users:** pin an issue (and a Discussions announcement) saying which version is affected, what goes wrong and what to do: usually "uninstall it from Settings > Apps", or the manual removal in the README if the uninstaller itself is the problem. For a security problem, publish a GitHub security advisory instead of an issue.
3. **Fix forward:** fix on `main` with a test that reproduces the problem, bump the patch version, add a CHANGELOG entry, and release it through the steps above. A published tag is never moved or reused.
4. If the release published something it must not (a secret, a private file), delete the release, rotate the secret, ask GitHub Support to purge cached copies, and treat it as a security incident: downloads and attestations already made cannot be recalled.

Setup installs over an earlier version, so a fixed version reaches users by running the new setup; there is no automatic update in v0.1.
