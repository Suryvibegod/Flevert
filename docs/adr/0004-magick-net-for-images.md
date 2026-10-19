# ADR-0004: Use Magick.NET for image operations, locked down by policy

## Status

Accepted

## Date

2026-10-19

## Context

The image module converts between JPG, PNG, WebP, AVIF, GIF, BMP and TIFF, reads HEIC/HEIF, compresses, and removes metadata. The most requested case is an iPhone HEIC photo that Windows cannot open or send, so reading HEIC is the deciding requirement. Everything runs offline on the user's machine, on images the user may have downloaded from anywhere, so the library is also the module's main attack surface.

## Decision

Use **Magick.NET-Q8-x64 15.0.0** (Apache-2.0, ImageMagick 7.1.2), one NuGet package with the native library for win-x64.

ImageMagick reads many formats, including scripts and formats that reach other files (MSL, MVG, TEXT, URL, `@file` lists), so it is locked down before first use:

- A `policy.xml` written on every start that denies all coders, delegates and filters, then allows only the formats this module reads or writes; `@*` and `fd:*` paths are denied. The folder holding it (and ImageMagick's temp files) is restricted to the user, SYSTEM and Administrators, so another account cannot relax the policy.
- Resource limits: 40 000 px per side, 400 MP per image, 2 GB memory, 4 GB disk, 1000 frames, 120 s.
- The decoder is chosen from the file extension, never sniffed from the content, so a `.jpg` that holds a script is an error.

## Alternatives Considered

### NetVips (libvips)

- Pros: faster and far lighter on memory for large images.
- Cons: LGPL-3.0; the Windows native build does not include a HEIC (HEVC) decoder, which is the number-one need.
- Rejected.

### Windows Imaging Component (WIC)

- Pros: built into Windows, no extra binaries.
- Cons: HEIC needs the paid HEVC extension from the Store; no AVIF or WebP encoder out of the box; more P/Invoke code.
- Rejected.

### SkiaSharp or ImageSharp

- Pros: managed-friendly APIs.
- Cons: no HEIC reading; ImageSharp's licence is not free for all commercial use.
- Rejected.

## Consequences

- About 25 MB of native code ships with the app; the installer compresses it.
- Known limitations, recorded in SPEC-image: ImageMagick's TIFF writer drops EXIF on `Convert > TIFF`; the "not smaller" log note for Compress waits for a core change that lets operations add a note to their log line.
- Security depends on the policy staying an allow-list; any new format must be added to it explicitly, and tests check that dangerous coders stay blocked even when named explicitly.
