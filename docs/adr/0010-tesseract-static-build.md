# ADR-0010: Static Tesseract 5.5.3 standalone binary built with vcpkg

## Status

Accepted

## Date

2026-10-21

## Context

v1.4 adds OCR capabilities (docs/specs/ocr.md) to extract text, detect page orientation, and generate searchable PDF text layers for Vietnamese and English documents. Documents originate from untrusted sources (email, web, chat), so OCR parsing must be robust, resource-bounded, and maintained with current security patches.

Flevert is licensed under MIT and avoids linking or bundling GPL/LGPL code where permissible alternatives exist (CLAUDE.md). Third-party binaries are never committed to the repository and must be fetched by SHA256-pinned hashes (ADR-0005, ADR-0008, ADR-0009).

Options evaluated on 2026-10-21:

- **Official UB Mannheim Windows installer (5.5.0 / 5.5.3)**: Distributed as an NSIS installer requiring 7-Zip unpacking. It statically links `jbigkit` (GPL v2) into Leptonica and ships dynamic LGPL runtime DLLs (`libiconv-2.dll`, `libintl-8.dll`, `libunistring-5.dll`, `libidn2-0.dll`). Bundling this would trigger GPL/LGPL redistribution obligations for the installer. The maintainer chose on 2026-10-21, and confirmed on 2026-10-22, to avoid GPL/LGPL libraries for the OCR module.
- **NuGet TesseractOCR (5.5.0)**: Bundles native dependencies from older builds that include outdated libraries with known security vulnerabilities (e.g. `libtiff 4.3.0`, `libpng 1.6.37`, `zlib 1.2.11`). Because Flevert processes untrusted image files, shipping vulnerable C/C++ image decoders poses severe security risks.
- **Windows.Media.Ocr**: Built-in Windows OCR runtime. It does not support the Vietnamese language pack on standard Windows installations, failing the core functional requirement of docs/specs/ocr.md.
- **PaddleOCR / Deep Learning models**: High accuracy but requires a Python runtime, heavy ONNX/DirectML dependencies, and multi-hundred-megabyte model downloads (>300 MB), exceeding the lightweight right-click shell extension size budget.
- **Building Tesseract via Microsoft vcpkg**: vcpkg provides ports for Tesseract 5.5.3 and Leptonica 1.87.0 allowing a completely static MSVC build (`/MT`) from permissive libraries only: the image codecs (libpng, libjpeg-turbo, libwebp, openjpeg, libtiff, zlib, giflib), plus libcurl and libarchive, which the port requires. No GPL (`jbigkit`) or LGPL (`libiconv`, `libintl`) component is built.

## Decision

- **Build standalone Tesseract 5.5.3 on GitHub Actions**: Automation defined in `.github/workflows/build-tesseract.yml` and `scripts/build-tesseract.ps1` builds `tesseract.exe` using Microsoft vcpkg on `windows-latest`.
- **Pinned source commits**:
  - vcpkg repository pinned to tag `2026.10.16`, dereferenced commit `9eec002df1cd109f8f104cd966a763daf9751489` (port `tesseract 5.5.3`, `leptonica 1.87.0`).
  - Fast language models from `tesseract-ocr/tessdata_fast` tag `4.1.0` (`vie.traineddata`, `eng.traineddata`, `osd.traineddata`).
  - Searchable PDF assets from `tesseract-ocr/tesseract` tag `5.5.3` (`pdf.ttf` glyphless font and `configs/pdf`).
- **Custom overlay triplet (`x64-windows-static-release.cmake`)**:
  ```cmake
  set(VCPKG_TARGET_ARCHITECTURE x64)
  set(VCPKG_CRT_LINKAGE static)
  set(VCPKG_LIBRARY_LINKAGE static)
  set(VCPKG_BUILD_TYPE release)
  ```
  Forces static library linkage, static MSVC C runtime (`/MT`), and release-only compilation to eliminate debug runtime overhead and external MSVC DLL dependencies.
- **Permissive dependencies only**:
  - The tesseract port requires libcurl and libarchive, which are linked in. The first build (2026-10-22, `vcpkg list` in the release's README.txt) has libcurl 8.23.0 on Schannel, libarchive 3.8.9 with bzip2, LZ4, liblzma, Zstandard, XAR and Windows CNG, and OpenSSL 3.6.5 installed with them: curl, BSD, 0BSD and Apache-2.0 licences, none copyleft. libcurl lets `tesseract.exe` read an `http(s)://` input name and libarchive a list of images, which is why Flevert only ever passes it image files it wrote itself (`Tesseract.cs`), never a name or list a stranger chose. A security fix in any of them means a new build and a new pin.
  - Leptonica statically links `giflib`, `libjpeg-turbo`, `libpng`, `libwebp`, `openjpeg`, `tiff`, and `zlib` (all under Apache-2.0, BSD-3-Clause, BSD-2-Clause, MIT, or zlib licenses).
  - Leptonica feature `jbigkit` (GPL) is omitted.
  - No GNU `libiconv` or `libintl` (LGPL) are included; standard Windows and C++ UTF-8 runtime facilities are used.
- **Automated verification on runner**:
  - `dumpbin /dependents tesseract.exe` validates that every imported DLL is a signed Microsoft Windows system DLL from `%SystemRoot%\System32` (verified via `Get-AuthenticodeSignature`). Visual C++ runtime DLLs (`vcruntime140.dll`, `msvcp140.dll`, `vcomp140.dll`, `concrt140.dll`, which the runner may have in System32 but a user's PC may not) and third-party DLLs cause an immediate build failure.
  - `tesseract --version` confirms exact versions: `tesseract 5.5.3` and `leptonica-1.87.0`.
  - Smoke OCR test dynamically draws a bitmap with Vietnamese characters ("Cộng hòa xã hội"), executes OCR with `vie.traineddata`, and asserts recognition accuracy before packaging.
- **Supply chain security and distribution**:
  - Build provenance attested using GitHub Actions `actions/attest-build-provenance` in an isolated job with `id-token: write` and `attestations: write`.
  - Published to repository releases under tag `deps-tesseract-5.5.3` as a pre-release (`--prerelease --latest=false`). Release creation verifies immutability (`gh release view` check).
  - Release archive `tesseract-5.5.3-vcpkg-2026.10.16-win64-static.zip` packages `tesseract.exe`, all port copyright notices (`licenses/*-copyright`), and a `README.txt` generated from `vcpkg list`.
- **Consumption in Flevert**:
  - `scripts/deps.json` pins the zip download, traineddata files, and PDF assets by SHA256 over HTTPS.
  - `scripts/fetch-deps.ps1` downloads to a private directory with restrictive ACLs, verifies SHA256 before extraction, and populates `deps/tesseract/`.
  - `scripts/write-notices.ps1` collects Apache-2.0 and port copyright files into `THIRD-PARTY-NOTICES.txt`.
  - `ExternalTool` runs `tesseract.exe` as a sandboxed child process with a memory cap (1 GB), single-process constraints, and cancellation tokens.
- **Maintenance**: Updates occur by updating the pinned vcpkg commit in `scripts/build-tesseract.ps1` and re-running the workflow by hand from `main` (`gh workflow run build-tesseract.yml --ref main`); it fails if the release tag already exists, so a new version needs a new tag in the workflow and a new pin in `scripts/deps.json`.

## Consequences

- Completely clean license profile: zero GPL/LGPL code shipped, satisfying Flevert's MIT distribution model.
- Minimal deployment footprint: standalone static `tesseract.exe` (6.8 MB; 3.3 MB zipped, with every port's licence) requires no side-by-side CRT DLLs or codec runtime files.
- Cryptographically attested build provenance directly verifiable via GitHub CLI (`gh attestation verify`).
- Fast and reliable OCR execution with official lightweight `tessdata_fast` models.
