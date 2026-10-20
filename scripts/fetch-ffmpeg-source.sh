#!/bin/bash
# Writes the complete corresponding source of the ffmpeg build Flevert ships (GPL v3, section 6), and the licence files
# of every library built into it, from the pins under "ffmpegSource" in scripts/deps.json. docs/adr/0009-ffmpeg-gpl-build.md.
#
#   scripts/fetch-ffmpeg-source.sh <output folder>
#
# Linux, with bash, git, jq, xz, tar and Docker: the release workflow runs it on ubuntu-latest, a maintainer on WSL. It
# builds BtbN's tooling image from the pinned commit of BtbN's repository and runs, in it, BtbN's own download.sh: the
# script BtbN fills its build cache with, so every library is fetched exactly as the build fetched it (git commits, SVN
# revisions, submodules, vendored Rust crates). Nothing is taken from a moving tag.
#
# Writes into <output folder>, which must not exist:
#   ffmpeg-source-<tag>-<n>-of-<count>.tar
#                             FFmpeg at its commit, BtbN's build scripts and patches at theirs, one tar.xz per library
#                             the build enables, and README.txt saying how they fit together: in as many tars as keep
#                             each under GitHub's 2 GiB per release asset, each whole on its own, all extracted into
#                             one folder giving the same tree
#   licences/<stage>-<file>   the licence files of FFmpeg and of each library, named after its stage in scripts.d, for
#                             installer/notices/ffmpeg (copy them there and commit; scripts/write-notices.ps1 puts them
#                             in THIRD-PARTY-NOTICES.txt under those names)
#   licences-missing.txt      the libraries with no licence file at their top, whose terms a person writes out there
set -euo pipefail
shopt -s nullglob

if [[ $# -ne 1 ]]; then
    echo "Usage: $0 <output folder>" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$1"
if [[ -e "$OUT" ]]; then
    echo "$OUT exists: give a new folder." >&2
    exit 2
fi

pin() { jq -er ".ffmpegSource.$1" "$ROOT/scripts/deps.json"; }
TAG="$(pin tag)"
BUILDS="$(pin builds)"
BUILDS_COMMIT="$(pin buildsCommit)"
TARGET="$(pin target)"
VARIANT="$(pin variant)"
ADDIN="$(pin addin)"
FFMPEG="$(pin ffmpeg)"
FFMPEG_COMMIT="$(pin ffmpegCommit)"
FFMPEG_VERSION="$(pin ffmpegVersion)"

WORK="$(mktemp -d)"
trap 'rm -rf -- "$WORK"' EXIT
mkdir -p "$OUT/licences" "$WORK/source/libraries"
OUT="$(cd "$OUT" && pwd)"

# A shallow fetch of exactly one commit, which must be the one asked for: a server cannot hand over another.
fetch_commit() { # <repository> <commit> <folder>
    git init -q "$3"
    git -C "$3" fetch -q --depth=1 "$1" "$2"
    local fetched
    fetched="$(git -C "$3" rev-parse FETCH_HEAD)"
    if [[ "$fetched" != "$2" ]]; then
        echo "$1 gave $fetched for $2." >&2
        exit 1
    fi
    git -C "$3" -c advice.detachedHead=false checkout -q FETCH_HEAD
}

echo "BtbN/FFmpeg-Builds at $BUILDS_COMMIT ($TAG)"
fetch_commit "$BUILDS" "$BUILDS_COMMIT" "$WORK/builds"
git -C "$WORK/builds" archive --format=tar --prefix=FFmpeg-Builds/ HEAD | xz -T0 > "$WORK/source/FFmpeg-Builds-$BUILDS_COMMIT.tar.xz"

echo "FFmpeg at $FFMPEG_COMMIT ($FFMPEG_VERSION)"
fetch_commit "$FFMPEG" "$FFMPEG_COMMIT" "$WORK/ffmpeg"
git -C "$WORK/ffmpeg" archive --format=tar --prefix=ffmpeg/ HEAD | xz -T0 > "$WORK/source/ffmpeg-$FFMPEG_VERSION.tar.xz"
for file in COPYING.GPLv3 COPYING.GPLv2 COPYING.LGPLv3 COPYING.LGPLv2.1 LICENSE.md; do
    cp "$WORK/ffmpeg/$file" "$OUT/licences/ffmpeg-$file"
done

# The tooling image of that commit, under the name download.sh looks for (util/vars.sh: REGISTRY_OVERRIDE and
# GITHUB_REPOSITORY choose it), so no image is pulled by a moving tag.
export REGISTRY_OVERRIDE=flevert.local GITHUB_REPOSITORY=btbn-pinned
docker build -q -t "flevert.local/btbn-pinned/base:latest" "$WORK/builds/images/base" > /dev/null
echo "Running BtbN's download.sh"
(cd "$WORK/builds" && ./download.sh > "$WORK/download.log" 2>&1) || { tail -n 50 "$WORK/download.log" >&2; exit 1; }

# The stages the asset's build enables, as BtbN's generate.sh decides it: each script's ffbuild_enabled, for this target,
# variant and addin. download.sh fetched every stage of every target; only these are part of the build.
STAGES="$(cd "$WORK/builds" && bash -c '
    set -e
    source util/vars.sh "$1" "$2" "$3" > /dev/null
    for STAGE in scripts.d/*.sh scripts.d/*/*.sh; do
        (
            SELF="$STAGE"
            STAGENAME="$(basename "$STAGE" | sed "s/.sh$//")"
            source util/dl_functions.sh
            source "$STAGE"
            ffbuild_enabled || exit 0
            if [[ -n "$(ffbuild_dockerdl)" ]]; then echo "$STAGENAME"; fi
        )
    done' stages "$TARGET" "$VARIANT" "$ADDIN")"

missing=()
for STAGE in $STAGES; do
    archive="$WORK/builds/.cache/downloads/$STAGE.tar.xz"
    if [[ ! -e "$archive" ]]; then
        echo "download.sh wrote nothing for $STAGE." >&2
        exit 1
    fi
    cp -L "$archive" "$WORK/source/libraries/$STAGE.tar.xz"
    # Licence files anywhere in the library's source: at its top, and those of the code it bundles (vendored Rust
    # crates, third_party/ and subprojects/ folders), which are linked into ffmpeg too (doubt-driven review, DD-6); the
    # texts of a LICENSES/ folder (the REUSE layout) and FreeType's, which its LICENSE.TXT only points to (re-check
    # review, RC-4). Never source files named so (notice.c, license.rs, OpenSSL's copyright.pm), configuration (shaderc's
    # license-checker.cfg), rich-text copies made for an installer (krb5's license.rtf, beside its plain NOTICE), nor the
    # library's history or test data: the notices hold licence texts only.
    mapfile -t licences < <(tar -tJf "$archive" \
        | grep -iE '(^|/)(COPYING|COPYRIGHT|LICEN[CS]E|NOTICE|UNLICENSE)[^/]*$|(^|/)LICEN[CS]ES/[^/]+$|(^|/)docs/(FTL|GPLv2)\.TXT$' \
        | grep -viE '/\.git/|/(tests?|testdata|test-data)/|\.(c|cc|cpp|cxx|h|hh|hpp|rs|py|js|ts|go|java|m|mm|s|asm|pl|pm|rb|sh|cmake|am|ac|in|cfg|json|toml|ya?ml|xml|html?|rtf)$' \
        || true)
    if [[ ${#licences[@]} -eq 0 ]]; then
        # Some keep their licence only in their source files' headers (nv-codec-headers): a person reads those.
        missing+=("$STAGE")
        continue
    fi
    mkdir -p "$WORK/licence/$STAGE"
    tar -xJf "$archive" -C "$WORK/licence/$STAGE" "${licences[@]}"
    for licence in "${licences[@]}"; do
        # Flattened: lame/COPYING of stage 50-libmp3lame becomes 50-libmp3lame-lame-COPYING.
        cp "$WORK/licence/$STAGE/$licence" "$OUT/licences/$STAGE-$(sed 's|^\./||; s|/|-|g' <<< "$licence")"
    done
done

if [[ ${#missing[@]} -gt 0 ]]; then
    printf '%s\n' "${missing[@]}" > "$OUT/licences-missing.txt"
    echo "No licence file at the top of: ${missing[*]} (listed in $OUT/licences-missing.txt): write out their terms by hand." >&2
fi

# GitHub refuses a release asset of 2 GiB or more: the archives are spread over as many tars as keep each below that,
# with room for tar's own headers. The first holds FFmpeg and BtbN's scripts; libraries fill the parts in name order.
LIMIT=$(( (2048 - 64) * 1024 * 1024 ))
cd "$WORK/source"
parts=("FFmpeg-Builds-$BUILDS_COMMIT.tar.xz ffmpeg-$FFMPEG_VERSION.tar.xz")
used=$(stat -c %s FFmpeg-Builds-"$BUILDS_COMMIT".tar.xz ffmpeg-"$FFMPEG_VERSION".tar.xz | awk '{ s += $1 } END { print s }')
for library in libraries/*.tar.xz; do
    size=$(stat -c %s "$library")
    if (( size > LIMIT )); then
        echo "$library alone is too large for one GitHub release asset." >&2
        exit 1
    fi
    if (( used + size > LIMIT )); then
        parts+=("")
        used=0
    fi
    parts[-1]+=" $library"
    used=$((used + size))
done
part_name() { echo "ffmpeg-source-$TAG-$(($1 + 1))-of-${#parts[@]}.tar"; }

cat > "$WORK/source/README.txt" <<EOF
The complete corresponding source of the ffmpeg build in Flevert (ffmpeg\\ in its install folder):
$(jq -r '.items[] | select(.name == "ffmpeg") | .url' "$ROOT/scripts/deps.json")

FFmpeg-Builds-$BUILDS_COMMIT.tar.xz
    BtbN's build scripts and patches at the commit of release $TAG, which built that asset:
    ./build.sh $TARGET $VARIANT $ADDIN
ffmpeg-$FFMPEG_VERSION.tar.xz
    FFmpeg at commit $FFMPEG_COMMIT, the source build.sh compiles.
libraries/<stage>.tar.xz
    Each library the build enables, as BtbN's download.sh fetched it for that stage of scripts.d. build.sh reads them
    from .cache/downloads/<stage>.tar.xz.

$(for i in "${!parts[@]}"; do
    part_name "$i"
    # shellcheck disable=SC2086 # one line per file of the part
    printf '    %s\n' ${parts[i]}
done)

Each of those tars holds this README.txt too: extract them all into one folder to get the tree above.

Written by scripts/fetch-ffmpeg-source.sh in Flevert's repository: https://github.com/Suryvibegod/Flevert
EOF

for i in "${!parts[@]}"; do
    # shellcheck disable=SC2086 # the part's file names, which hold no spaces (stage names)
    tar -cf "$OUT/$(part_name "$i")" README.txt ${parts[i]}
    echo "$OUT/$(part_name "$i"): $(( $(stat -c %s "$OUT/$(part_name "$i")") / 1000000 )) MB"
done
echo "$(wc -w <<< "$STAGES") libraries"
