# ADR-0006: Tell users about new versions by asking GitHub Releases, and never update by itself

## Status

Accepted

## Date

2026-10-20

## Context

Flevert is installed from a setup file. Nothing tells a user that a newer version exists, so an old version, perhaps with a security problem that a later release fixed (CHANGELOG lists one in ImageMagick), would stay installed for good. The maintainer requires that a user of v0.1.0 is asked to update once v0.2.0 is out. That means v0.1.0 itself must check.

Flevert also promised to work offline, without telemetry, and its releases are unsigned (ADR-0005). That rules out a silent auto-updater: it would download and run an unsigned executable on the strength of a network response.

## Decision

- After a batch that shows the progress window or notification, the leading `flevert.exe` asks `https://api.github.com/repos/Suryvibegod/Flevert/releases/latest`, at most once every 24 hours. The request carries only `User-Agent: Flevert/<version>`. It has a 5-second timeout and reads at most 1 MB. Only `tag_name` is used. The endpoint ignores drafts and pre-releases, so a release withdrawn by marking it pre-release (RELEASING) is not offered.
- A strictly formed newer tag (`vMAJOR.MINOR.PATCH`, ASCII digits) that the user has not skipped brings up a Windows task dialog after the batch: **Download** opens the fixed page `https://github.com/Suryvibegod/Flevert/releases/latest` in the browser; **Later**; **Skip this version**. No URL from the response is ever opened, and nothing is downloaded or run. The user downloads the new setup and checks it as the README says.
- The check can be turned off with `flevert.exe update-check off` and by a check box in setup (on by default, `/UPDATECHECK=0|1`, remembered across installs). The state lives in `%LOCALAPPDATA%\Flevert\update\state.json` and is removed on uninstall.
- Every failure is silent and never affects the batch or how fast the process exits.

## Alternatives considered

- **Silent auto-update** (download and run the new setup): rejected. It turns a network response into code execution, the releases are unsigned, and it would replace files while Explorer may start `flevert.exe`.
- **Notification balloon instead of a dialog**: easy to miss, and Focus Assist suppresses balloons, while the maintainer wants users to be asked. The batch result keeps its balloon; the update question is a separate dialog, at most once a day.
- **Checking at every run, or in a background service**: more requests and a resident process for no gain; a daily check after real use is enough.
- **Off by default (opt-in)**: would leave most users on old versions, which is the problem being solved. The setup page says what the check sends and lets the user turn it off.

## Consequences

- Flevert is no longer fully offline: README and CHANGELOG say what the one request sends and how to turn it off. GitHub sees the user's IP address and the Flevert version, at most once a day.
- A release becomes visible to users only when it is published and is "latest"; RELEASING's way of withdrawing a bad release (mark it pre-release) also stops it being offered.
- Unauthenticated GitHub API calls are limited to 60 per hour per IP address; one call a day per user is far below that. If the limit is hit, the check fails silently and runs again a day later.
- The repository name is part of the URL compiled into every version. Renaming or moving the repository needs GitHub's redirect to keep working for old versions, so it should be avoided.
