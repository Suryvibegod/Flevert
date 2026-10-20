# ADR-0009: Video and audio with the GPL build of ffmpeg, run as a separate program

## Status

Accepted

## Date

2026-10-20

## Context

v0.3 converts, compresses and extracts video and audio (docs/specs/media.md). Videos arrive from phones and strangers, so whatever parses them must be maintained and patched; ffmpeg is the only engine that reads every source type of the spec. Flevert is MIT and never links GPL code into its own process (CLAUDE.md). Binaries are never committed and releases are built only by GitHub Actions (ADR-0005).

Options checked on 2026-10-20:

- **BtbN/FFmpeg-Builds** publishes Windows builds of ffmpeg in GPL and LGPL variants, static or shared, for each release branch. Daily builds are kept 14 days; the last build of each month is kept two years (its README, "Release Retention Policy"); `latest` moves every day. Each release lists the SHA256 of its assets in `checksums.sha256`.
- The **LGPL** variant has no libx264. H.264 would then come from `h264_mf` (Media Foundation): no constant quality, no two passes, and quality that depends on each machine's encoder, so "Under 25 MB" could be neither promised nor tested.
- Both variants oblige whoever distributes them to provide the corresponding source (LGPL 2.1 sections 4 and 6 for ffmpeg and its LGPL libraries; the GPL variant adds x264 and the other GPL libraries).

## Decision

- **The GPL build**, shared variant, of **FFmpeg 8.1.3** (`n8.1.3-9-g29e619e767`) from BtbN's release `autobuild-2026-09-30-13-08`, the last build of September: `ffmpeg-n8.1.3-9-g29e619e767-win64-gpl-shared-8.1.zip`, SHA256 `dfe81c3aa0a546ee81980b1b824486dd0fe0a1d452f00cb55135379a74be7bcf` (as in the release's `checksums.sha256`; downloaded and computed again on 2026-10-20). The user approved GPL over LGPL on 2026-10-20 (docs/specs/media.md, Open Question 1). The build's own licence is GPL version 3 (`--enable-version3`).
- `scripts/fetch-deps.ps1` fetches it into `deps/ffmpeg` like pdfium and qpdf (ADR-0008) and keeps `ffmpeg.exe`, `ffprobe.exe`, the seven DLLs they share (199 MB unpacked, about 46 MB more in the installer) and `LICENSE.txt`; not `ffplay.exe`, headers or import libraries. `Flevert.Media.csproj` copies them into an `ffmpeg` folder beside `flevert.exe`, and the installer installs that folder without unpacking it for setup's location check. CI checks that the version line of the fetched `ffmpeg.exe` names the pinned commit.
- **A separate program.** Flevert only starts `ffmpeg.exe` and `ffprobe.exe` through `ExternalTool` (a job that dies with flevert.exe, a timeout, below normal priority) and reads their output. Nothing of ffmpeg is linked into Flevert, which stays MIT: the two are an aggregate.
- **Untrusted input**, because the build carries network protocols (libcurl, OpenSSL, libssh, SRT, RIST, ZeroMQ) and script demuxers (AviSynth, VapourSynth):
  - the input is always `file:<full path>`, after `-protocol_whitelist file`, so its name can never select a protocol and nothing it contains can open one;
  - `-format_whitelist mov,matroska,avi,asf,mpegts,mp3,wav,flac,ogg,aac,aiff` allows only the demuxers of the spec's source types (ffmpeg matches it with `av_match_list`, so `mov` is `mov,mp4,m4a,3gp,3g2,mj2`), so a playlist, a concat list or a script named `.mp4` is refused by ffprobe before anything is converted (MaliciousMediaTests; without the demuxer whitelist, a concat list naming the real video beside it is read);
  - every option is Flevert's own (constants and numbers it computed), passed one by one; nothing a file says reaches the command line except metadata values, each a single `key=value` argument, from an allowlist of tags;
  - ffprobe refuses a file with no stream, no duration, more than 12 hours or a picture past 8K; each pass has a timeout of six times the duration plus a minute (twelve for VP9); `-fs` stops an output past four times the input plus 100 MB (or twice what the duration calls for), and reaching it is an error;
  - `FFREPORT` and `AV_LOG_FORCE_*` are removed from ffmpeg's environment.
- **The corresponding source.** `scripts/fetch-ffmpeg-source.sh` writes `ffmpeg-source-<tag>.tar` from the pins under `ffmpegSource` in `scripts/deps.json`: FFmpeg at commit `29e619e767cde9045a75c29bc9a8278ae7b3a98b`, BtbN's build scripts and patches at `6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b` (the commit of the release tag), and each of the 101 libraries the `win64 gpl-shared 8.1` build enables, fetched by BtbN's own `download.sh` in BtbN's tooling image built from that commit, so every library comes from the git commit, SVN revision, submodules and vendored crates the build used. `release.yml` runs it on Linux (Docker) through `ffmpeg-source.yml` and attaches the archive to every release beside the setup, with a line in the release notes; GitHub's limit of 2 GiB per asset is checked. This replaces the `scripts/fetch-ffmpeg-source.ps1` the spec named: BtbN's download needs Docker with Linux containers, which the Windows runners lack.
- **Licences.** The same script collects the licence files of FFmpeg and of each library (`COPYING*`, `LICENSE*`, `COPYRIGHT*`, `NOTICE*` at the top of its source); they are committed under `installer/notices/ffmpeg/`, named after the build stage (`50-x264-COPYING`), and `scripts/write-notices.ps1` puts them in `THIRD-PARTY-NOTICES.txt` after the FFmpeg entry. The libraries whose terms live only in their source files' headers (nv-codec-headers) are listed by the script, and their terms are written out there by hand. `ffmpeg-source.yml` runs on every pull request that changes the pin, uploads the licences as an artifact, and fails while a licence file of the build is missing from `installer/notices/ffmpeg/` or differs; the release fails the same way.

## Consequences

- The installer grows from 47 MB to about 94 MB (LZMA2, measured on the ffmpeg folder with `xz -9e`); docs/specs/media.md allows 150 MB.
- Each release carries a source archive of possibly a gigabyte or more, written by a two-hour job; the BtbN tooling image is built from the pinned commit, its base image and packages from their archives on the day.
- Dependabot cannot follow BtbN's builds: a monthly check for a new FFmpeg release (docs/RELEASING.md) moves the pin to the newest month-end build, with its SHA256, the `ffmpegSource` pins and the licences the pull request's artifact brings.
- A month-end build is deleted after two years, so a release older than that can no longer be rebuilt from the pin; its source archive stays attached to it.
- Unicode paths, `\\?\` long paths, x264's statistics file under a Vietnamese user name, the priority, and ffmpeg dying with flevert.exe are checked on Windows only (FlevertExeTests, test-installer.ps1): the media tests run elsewhere with the system's ffmpeg and without job objects.
