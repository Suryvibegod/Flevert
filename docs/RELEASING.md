# Releasing Flevert

For maintainers. Releases are built only by [`.github/workflows/release.yml`](../.github/workflows/release.yml), from a tag on `main`; nothing built on a personal machine is ever attached to a release ([ADR-0005](adr/0005-inno-setup-unsigned-until-signpath.md)).

## Before tagging

1. `<Version>` in `Directory.Build.props` is the new version (`MAJOR.MINOR.PATCH`, nothing after it). In `CHANGELOG.md`, the `[Unreleased]` section is renamed to `[X.Y.Z] - <date>`, a new empty `[Unreleased]` goes above it, and the links at the bottom are updated. The verify commands in [docs/install.md](install.md#check-your-download) name a version as an example; change it if they should show the new one. The README lists only what the release really does.
2. The repository is clean (below).
3. CI is green on the commit three times. The runs may go at the same time, each on its own runner: `gh workflow run ci.yml --ref main` three times, then check that all three ran on the tagged commit and passed. A flaky test shows up either way; one red run means fix and start again.
4. The three reviews (code, security, doubt) of every change since the last release found nothing Critical, High or Required. The scope is `git diff v<previous>..HEAD`: features released and verified before are not reviewed again unless their code changed, but shared code that changed (runner, batching, output paths, setup, menus) is in scope because it touches old features too. The manual runtime check covers what is new or changed; every automated test still runs, as on CI, to catch regressions, and the upgrade over the previous release (step 5) is always tried.
5. Once a release has been published before this one, upgrade over it: install the previous published setup, convert a file, run the new setup over it (from the draft, below), and check the menu still works and uninstalling removes everything. `scripts/test-installer.ps1` does this in its last scenario: it gets the previous setup through `scripts/get-previous-setup.ps1`, which downloads it (or copies the file given with `-PreviousSetup`) and refuses it unless its SHA256 is the one pinned there.

## Clean the repository

Visitors judge a project by its branch list, its open pull requests and its root folder. Before tagging, and once more after publishing:

1. **Branches:** every `wip/*` or feature branch, except the one `tasks/HANDOFF.md` names as current work, is merged into `main` (straight history) or dropped, then deleted on GitHub and locally (`git push origin --delete <branch>`, `git branch -D <branch>`). `git worktree list` shows only the main checkout (agent worktrees are locked: once `git -C <path> status` shows nothing to keep, `git worktree remove -f -f <path>`, then `git worktree prune`). Local backup branches go once the release is out. Once the pull requests (step 2 below) are done, `git ls-remote --heads origin` lists only `main`, plus the current-work branch if `tasks/HANDOFF.md` names one.
2. **Pull requests:** every Dependabot pull request is merged or closed with a reason (see [Dependency updates](#dependency-updates)); none stays open across a release.
3. **Files:** `git status --ignored` shows only build output, `deps/` and local files under `.claude/` (worktrees, `settings.local.json`, locks); nothing stray is tracked at the root (notes, probes, logs, personal files such as `AGENTS.md`).
4. **Docs:** README, CHANGELOG, `docs/install.md` and the specs describe the release as shipped; `tasks/HANDOFF.md` marks the finished module as done and drops notes that no longer apply.
   - **No hand-over traces** (the maintainer's rule): `tasks/HANDOFF.md` keeps only the goal, the process, the state and the next task. Sections written to move work between machines or sessions go: which computer the work moved to, tables of `wip/*` branches, "stopped mid-way" notes, agent and worktree instructions tied to one session, local folder names. `git grep -n -i -e "wip/" -e "bàn giao giữa chừng" -e "máy văn phòng" -e "máy PC" -e "worktree-agent" -- tasks/HANDOFF.md` prints nothing. The per-module `todo.md` files stay as the work's history.
5. **After publishing:** delete local build output (`artifacts/`) and stale branches that appeared during the release (a Dependabot branch, a fix branch). Pin the release just published in `scripts/get-previous-setup.ps1` (its version and the SHA256 from its `SHA256SUMS.txt`), so the next release's upgrade test starts from it.

## Tag and check the draft

1. Push `main` first, then `git tag -a vX.Y.Z -m "Flevert X.Y.Z"` and `git push origin vX.Y.Z`. Only repository administrators can create `v*` tags (ruleset "Release tags"). The workflow refuses a tag that is not `vMAJOR.MINOR.PATCH`, does not match `<Version>`, or is not on `main`.
2. The workflow tests, builds the installer, attests it and attaches it with `SHA256SUMS.txt` to a **draft** release. The release notes are the version's CHANGELOG section plus how to check the download; the workflow fails if the CHANGELOG has no section for the version.
3. Download the setup from the draft and check it as a user would:
   - `Get-FileHash` matches `SHA256SUMS.txt`.
   - The `gh attestation verify ...` command in the release notes passes. It pins the tag and the commit (`--source-digest`).
   - `powershell -File scripts/test-installer.ps1 -Setup <setup>` passes on a machine without Flevert. Running the tests leaves `%LOCALAPPDATA%\Flevert` behind, and the script refuses to run while it exists: delete it first, and do not run the tests at the same time.
   - The runtime in the installed `flevert.deps.json` (`runtimepack.Microsoft.NETCore.App.Runtime.win-x64/...`) is the newest .NET 10 patch.
   - Microsoft Defender and VirusTotal report nothing. A detection by one or two engines with a generic name is usually a false positive of an unsigned Inno Setup installer: report it to the vendor, and do not publish until it is understood.
4. Publish the draft with **Set as the latest release** ticked. From then on, installed copies that check for new versions offer it after their next conversion, once a day has passed since their last check (they ask GitHub for the latest release, which never includes drafts or pre-releases). Never rename or delete the GitHub account or this repository: installed copies ask for releases by that name (ADR-0006).

Until it is published, a draft may be thrown away and rebuilt: `gh release delete vX.Y.Z --cleanup-tag --yes`, then tag again. Delete the old run's artifacts too: the old build's attestation stays valid. The verify command in the release notes pins the commit (`--source-digest`), so a file built from any other commit fails it; the shorter command in `docs/install.md` pins only the tag, which is why that page points readers to the notes. Once published, the release is immutable (repository setting "Immutable releases"): its assets and tag cannot change, and the tag name can never be used again, even after the release is deleted.

## If the release workflow fails

- **Build or tests fail:** no draft exists. Fix the cause on `main` (through review, as any change), delete the tag (`git push --delete origin vX.Y.Z`; administrators bypass the tag ruleset) and tag the fixed commit. Never tag a commit that is not on `main`.
- **A flaky test, or the attest or release job fails** (network, GitHub outage): re-run the failed jobs from the run's page. Nothing has been published, and the build is not repeated if only a later job failed.
- **The draft exists but is wrong:** throw it away as above.

## If a published release is bad

Stop new downloads first, then fix forward. Users who already installed it keep working until they update or uninstall.

1. **Stop downloads:** the assets of a published release cannot be removed (immutable releases), so edit the release: mark it as a pre-release, which takes it off "latest" and stops installed copies offering it, and put a warning at the top of its notes. If it must disappear, delete the release; its tag name stays burnt. For the very first release the README's download link then shows no download until the fix is out.
2. **Tell users:** pin an issue (and a Discussions announcement) saying which version is affected, what goes wrong and what to do: usually "uninstall it from Settings > Apps", or the [manual removal](install.md#remove-flevert-by-hand) if the uninstaller itself is the problem. For a security problem, publish a GitHub security advisory instead of an issue.
3. **Fix forward:** fix on `main` with a test that reproduces the problem, bump the patch version, add a CHANGELOG entry, and release it through the steps above. A published tag is never moved or reused.
4. If the release published something it must not (a secret, a private file), delete the release, rotate the secret, ask GitHub Support to purge cached copies, and treat it as a security incident: downloads and attestations already made cannot be recalled.

Setup installs over an earlier version, so a fixed version reaches users by running the new setup; there is no automatic update in v0.1.

## Dependency updates

Dependabot proposes updates to GitHub Actions and NuGet packages weekly, a week after their release ([`.github/dependabot.yml`](../.github/dependabot.yml)). Merge one only when CI passes and its release notes show nothing surprising; an Action update must keep the commit pin. A Magick.NET update that fixes a security problem in ImageMagick gets a patch release with a CHANGELOG entry naming the advisory. Close updates that are not needed rather than letting them pile up.

Dependabot cannot see pdfium and qpdf, which `scripts/deps.json` pins by URL and SHA256 ([ADR-0008](adr/0008-pdf-engines-pdfium-and-qpdf.md)). Once a month, look for a newer [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries/releases) build and [qpdf](https://github.com/qpdf/qpdf/releases) release, and for security fixes in pdfium, qpdf, OpenSSL, zlib and libjpeg-turbo. To change a pin:

1. Check where the new file comes from, not only its hash: it is a release asset of the project's own repository (`bblanchon/pdfium-binaries`, `qpdf/qpdf`), built by that project's release workflow from a tagged commit, and the pinned SHA256 is the digest GitHub shows for the asset. Read the release notes and the changes to the build scripts since the last pin; a new maintainer, a changed build or signing setup, or an asset uploaded by hand is a reason to wait and ask upstream.
2. Update the URL, `sha256` and, for a new version, the licence files' tag in `deps.json`; run `scripts/fetch-deps.ps1` and the three test projects.
3. Update the `notices` in `deps.json` to what the new files contain: a renamed or added binary (a new MSVC runtime DLL, say) fails `NoticesScriptTests` and the installer build until it is listed with its licence, and a version named in a title must match the binary. qpdf links zlib, libjpeg-turbo and OpenSSL into `qpdf30.dll` at whatever versions vcpkg had on the day of its build: a new qpdf fails `QpdfDepsTests` when they changed, and their licence files are then pinned at the tags of the versions the binary names (ADR-0008 shows how they were checked for 12.4.2).
