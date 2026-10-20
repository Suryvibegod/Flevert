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

- A `policy.xml` checked on every start that denies all coders, delegates and filters, then allows only the formats this module reads or writes; `@*` and `fd:*` paths are denied. The folder holding it (and ImageMagick's temp files) is restricted to the user, SYSTEM and Administrators, so another account cannot relax the policy. Each configuration file is kept only when it holds Flevert's exact bytes in a file of its own that only those accounts can change; anything else is replaced atomically (see the amendment below).
- Resource limits: 40 000 px per side, 400 MP per image, 2 GB memory, 4 GB disk, 1000 frames, 120 s.
- ImageMagick never guesses a format. The module picks the decoder from the file's signature when it matches an allowed format (misnamed downloads are common), otherwise from the extension, so a `.jpg` that holds a script is an error.
- ImageMagick also reads `policy.xml` and `delegates.xml` from the working directory, `MAGICK_HOME`, `MAGICK_CONFIGURE_PATH`, `%LOCALAPPDATA%\ImageMagick` and the XDG/HOME config folders, and applies them after ours (a planted one can redirect the pixel cache or enable delegates). The module refuses to run if any ImageMagick configuration file exists in the user folders (also `%APPDATA%` and `%USERPROFILE%`), initialises with its own folder as the working directory, and then proves MVG, MSL and TEXT are refused by the coder rule. Files ImageMagick loads lazily after initialisation (colors, thresholds, type) are still looked up in the current working directory; flevert.exe always runs with its install folder as the working directory, and any other host must do the same.
- ImageMagick never sees a path: files are read and written through streams the module opens, because it reads `%d`, `[0]` and similar in names as patterns.

## Amendment (2026-10-20): configuration files kept when already exact

v0.1.0 replaced all eleven configuration files on every start (write a temporary file, then rename it over the old one). ImageMagick opens these files without delete sharing while it loads them, and Windows refuses to rename over a file that is open that way. When two different image operations started together (two batch leaders, for example Convert > PNG on some files and Compress on others), one process often failed with `UnauthorizedAccessException: Access to the path is denied` in `MagickSetup.WriteConfiguration` (11 of 20 rounds of six simultaneous processes in the reproduction).

Decision: compare before writing. A file is kept as it is when it holds exactly the bytes Flevert would write, and it is a file of its own: opened without following links, not a reparse point, one hard link, owned by and writable only by this user, SYSTEM or Administrators. The comparison reads with read and delete sharing, so it never blocks another process. Anything else (missing, stale, tampered, a link, permissions another account could use) is replaced as before, through a unique temporary file and an atomic rename; while the rename is refused because another flevert.exe is loading the file, the start retries for up to 5 seconds, accepting the file as soon as another process has put the exact bytes there. If it still cannot be replaced, the start fails and the stale file is not used; the temporary file is removed.

Why the security properties hold: before, Flevert wrote the bytes and ImageMagick read them a moment later; only accounts that can change the folder (the user, SYSTEM, Administrators) could act in between. Now Flevert reads and checks the bytes and ImageMagick reads them a moment later, with the same window and the same accounts. The link and permission checks keep what the rename used to guarantee as a side effect: the file Flevert accepts is one only those accounts can change, not a link to a file elsewhere with its own permissions. The probes of MVG, MSL and TEXT still run after loading.

Alternatives considered:

- **Rename with retry only:** every start would still rewrite eleven files and wait for whichever process is reading them, so simultaneous starts would keep contending; retry is kept only for the rare case where a file really must change (first run, update, tampering).
- **A named mutex for the session:** it would serialise writes, but ImageMagick loads some files (colors, type) lazily, after initialisation and outside any lock, so a writer could still meet a reader; holding the lock for a whole run would serialise every image operation.
- **Reading with delete sharing:** ImageMagick opens the files itself; Flevert cannot change how.

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
- Magick.Native bundles an HEVC decoder (libheif with libde265) to read HEIC. Its software licences allow free redistribution, but HEVC is covered by patents in some countries; "free" in this project refers to software licences, not patent rights. Windows' own HEIF/HEVC extensions are the alternative a user can choose.
- Known limitations, recorded in docs/specs/image.md: ImageMagick's TIFF writer drops EXIF on `Convert > TIFF`; the "not smaller" log note for Compress waits for a core change that lets operations add a note to their log line.
- Security depends on the policy staying an allow-list; any new format must be added to it explicitly, and tests check that dangerous coders stay blocked even when named explicitly.
