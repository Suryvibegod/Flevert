# ADR-0010: A Linux version with a Nautilus right-click menu

## Status

Proposed (a contributor's port, docs/specs/linux.md; the capability map listed Linux as out of scope until now)

## Date

2026-10-21

## Context

Linux desktop users have the same photos and PDFs to fix as Windows users. Core, image and pdf are .NET code that runs on Linux, but they lean on Windows in a few dozen places: the registry menu, WinForms, named pipes and job objects, ACLs and integrity labels, `CreateFileW` handles that lock files and folders, `StrCmpLogicalW`, and pdfium and qpdf built for Windows. The Windows app must not change.

Everything below was measured on Pop!_OS 22.04 (Nautilus 42, glibc 2.35, x86-64) while porting; each Linux behaviour has a test, and each test of Windows-only behaviour is marked `[WindowsFact]` or left out of the Linux build, never deleted.

## Decision

### Build

- `Directory.Build.props` targets `net10.0-windows` on Windows and `net10.0` elsewhere. `Flevert.Cli` and `MenuProbe` stay `net10.0-windows` everywhere (with `EnableWindowsTargeting`), so a Linux machine also catches Windows compile errors. `Flevert.Linux` uses the host target and builds as part of the solution on both systems; Linux packages still target `net10.0`. The shared libraries and Linux host disable only the automatically generated assembly platform attribute: their explicit platform annotations and CA1416 remain active, so a Windows build can analyze the Linux host without falsely declaring every shared API Windows-only.
- Windows-only types and members carry `[SupportedOSPlatform("windows")]`, the Linux ones `[SupportedOSPlatform("linux")]`, and shared code branches with `OperatingSystem.IsWindows()`/`IsLinux()`. CA1416 stays an error; nothing is silenced. The Linux side lives under `Unix/` folders.

### One process per selection

Nautilus hands every selected file to one call, so `flevert run` gets the whole batch at once and uses `BatchLeader.Alone`: no pipe, no census of processes, none of ADR-0002's coordination. More than 1000 files (a Windows batch's limit) are refused with a notification.

### Files

- **Inputs** are opened with `O_PATH|O_NOFOLLOW` and checked with `statx` (whose layout is the same on every architecture): only a regular file with one name is processed, never a link, FIFO, device or folder; `O_PATH` reads nothing, so a FIFO cannot block. Outputs are named after the path the kernel holds for the handle (`readlink /proc/self/fd/N`), but nothing reads the input by that name again: whoever can write the folder could point it at another file (a link to a private file, whose converted copy would land beside the input) after the check. Every engine reads through the pin itself, `/proc/<flevert>/fd/N` (`PinnedInput.ReadPath`): ImageMagick in the process, qpdf and the PDF worker (processes of the same user, which Yama's `ptrace_scope` 1 does not stop; it restricts attaching only). The worker's request carries both paths (format 3), and qpdf's messages show the name. A test swaps every input's name for a link to junk right after the pin, for every operation.
- **Folder outputs** (one-to-many) are made `mkdir 0700`, so nobody else can add to them while they are written; held open with `O_NOFOLLOW`; written through the handle (`/proc/<flevert>/fd/N`), so a link swapped in for the name is never written through; emptied through the handle (`unlinkat`); renamed only while their name still leads to the folder held (device and inode), with `renameat2(RENAME_NOREPLACE)`; then given the usual mode of a new folder (`0777` less the umask, read from `/proc/self/status`).
- **Names** fit 255 bytes of UTF-8 on Linux (255 UTF-16 characters on Windows), cut between characters, through one `OutputPath.FitName` that replaced five copies of the rule.
- Flevert's own folders are `0700`; ImageMagick's policy is written `0600` and kept only when it is a regular file of one name, owned by the user, writable by nobody else, with the exact bytes.
- `RunLog` locks the log with `FileShare.None` on Linux: .NET maps sharing to `flock`, where `FileShare.Read` is shared, and two parallel writers overwrote each other's lines.

### Tools (qpdf, the PDF worker, zenity)

A tool runs as `setsid` → `setpriv --pdeathsig KILL` → `prlimit --data=<limit>` → the tool, each exec'ing the next in place, all found at fixed paths, never through PATH:

- `setsid` gives it a process group of its own, killed when the run ends, is cancelled or times out: what the job object does on Windows.
- `setpriv --pdeathsig KILL` ends it when flevert dies. Linux sends that signal when the **thread** that started the process ends, so every tool is started from one thread that lives as long as flevert (`UnixStarter`); started from a thread-pool thread, a tool would die when that thread retired. Before this, workers spinning on a malicious page kept running at full CPU for over ten minutes after their parent died.
- `RLIMIT_DATA` limits memory, close to what a job counts as committed. `RLIMIT_AS` was tried first: .NET reserves tens of gigabytes of address space it never uses (35 GB with 27 MB resident), and the worker ran out under 2.5 GB.
- A refused allocation is reported to nobody on Linux. A tool counts as over its limit when it says so (`Out of memory`, `bad_alloc`, `memory exhausted`) or dies of a signal other than SIGKILL after using three quarters of the limit, by `VmData` sampled every 50 ms: pdfium aborts without a word when refused (a 4 GiB content-stream bomb ended with SIGABRT at 2.5 GB).

### PDF dependencies

`libpdfium.so` from bblanchon/pdfium-binaries (same branch as Windows) and qpdf's Linux zip, pinned by SHA256 in `scripts/deps-linux.json` and fetched by `scripts/fetch-deps-linux.py` (the guarantees of `fetch-deps.ps1`, and files taken out of an archive by name only). Of qpdf's zip only `qpdf`, `libqpdf` (Apache-2.0) and `libjpeg` (libjpeg-turbo, BSD/IJG) are kept; gnutls and zlib come from the system, so no LGPL library is redistributed. qpdf keeps its `bin/` and `lib/` (it finds libqpdf through `$ORIGIN/../lib`).

### Desktop

- **Order of joined files**: GLib's `g_utf8_collate_key_for_filename`, which Nautilus sorts with, called from the system's `libglib-2.0` (linked, not shipped) after `setlocale(LC_COLLATE, "")`. Its order follows the locale's collation (in en_US "img_0011" sorts before "IMG_0009"); no natural comparison of our own reproduced it. Without GLib, a natural comparison.
- **Watermark font**: the TrueType font fontconfig gives for Arial with Vietnamese (`fc-match`, Liberation Sans or Arimo, made to replace Arial). Flevert ships no font.
- **Progress, questions, notifications**: zenity (`--progress`, `--forms` with masked fields) and `gdbus` to `org.freedesktop.Notifications`, all separate processes, with GVariant and Pango markup escaped. A password goes from zenity through a pipe into a buffer and characters that are wiped, never a string or a command line.
- **Menu**: a nautilus-python extension (MIT, API 3.0 and 4.0) reads `menu.json`, which `flevert install-menu` builds for each extension with the Windows menu's own `MenuNode`, so the order is the same; the PDF tools Windows shows only with Shift are a submenu at the end, since Nautilus has no 16-entry limit. `install-menu` checks the whole app tree and its ancestors: trusted owners only, no links or files with multiple hard links, and no entries other accounts can write. Only ancestor folders may use the sticky-directory exception.
- **Package**: a tarball with `install.sh` and a Python-standard-library installer, installing for the user into `~/.local/share/Flevert/app` (0700) without root. `.flevert-install.json` records app entries. Unrecognized existing apps, linked paths and unsafe owners/permissions are refused before replacement or deletion. Updates refuse unknown app entries; uninstall keeps unknown files. Failed menu registration restores the previous app, manifest and menu files; if rollback fails, its backup is retained for recovery.

## Where Linux is weaker than Windows

- **No mandatory locks.** A pinned input, or a parent of the output folder, can still be renamed or replaced by whoever can write its folder. Flevert still reads the file it checked and writes into the folder it made, but an output is published beside the name, so someone who can write the input's folder can still move it or put something else in its way. The inputs are beside the user's own files, normally in their home; folders others can write are rare on a desktop. In `/tmp`, the kernel's `fs.protected_symlinks` and `fs.protected_hardlinks` (on by default) keep links planted by others from being followed. Without `RENAME_NOREPLACE` (a file system that lacks it), the rename falls back to check-then-rename, which could replace an empty folder created in between.
- **Only the tool dies with flevert**: what a tool starts gets no death signal (qpdf and the worker start nothing).
- **No single-process limit** for qpdf.
- **Memory refusals are inferred**, not reported (above). A tool that ends with an error after using three quarters of its limit counts as refused (so a large PDF qpdf repairs with warnings near its limit is refused rather than written: safe, but the message may be wrong); one large allocation refused early leaves `VmData` low, and pdfium's abort then reads as a crash: still a failure, with another message. `RLIMIT_DATA` covers `mmap` since Linux 4.7.
- **Older systems**: `setpriv --pdeathsig` needs util-linux 2.33; a tool started just before flevert dies, before `setpriv` sets the death signal, is not killed with it. qpdf's own `libjpeg` (libjpeg-turbo 2.1.2) comes with qpdf's official build and is updated with it.
- **The update check is off** on Linux: there is no Linux release to point to yet.
- **No video or audio.** `Flevert.Media` (1.3.0) and its tests are pinned to `net10.0-windows`, as `Flevert.Cli` is: Linux compiles them, so Windows-only compile errors show there too, but the Linux package and menu leave them out until ffmpeg for Linux has a spec of its own.
- **Desktop session and selection size.** Interactive use requires a working GNOME session; terminal/SSH jobs without a display use `FLEVERT_HEADLESS=1`. Nautilus passes selections through argv, so the operating system's argument-byte budget also applies to the 1000-file count limit; very deep paths may require smaller selections.

## Consequences

- Windows behaviour is unchanged; Windows-only tests are skipped on Linux with a reason, and Linux tests are skipped on Windows.
- Linux needs, on the desktop: util-linux, GLib (`gdbus`), zenity, fontconfig, and nautilus-python for the menu (`install.sh` and `install-menu` say how to install what is missing).
- x86-64 only for now: a few `open` flags differ on other architectures, and `Libc` refuses them rather than guess.
- glibc 2.34 and the libstdc++ of GCC 11 or later, which qpdf's Linux build needs (`GLIBC_2.34`, `GLIBCXX_3.4.29`): Ubuntu 22.04, Debian 12, Fedora 35 and later.
- Nautilus 43 and later (nautilus-python 4.0) are covered only by tests with a stand-in; the machine the port was made on has Nautilus 42.
