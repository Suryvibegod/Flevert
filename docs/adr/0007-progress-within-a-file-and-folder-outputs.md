# ADR-0007: Operations beyond one file in, one file out

## Status

Accepted (part 1: progress within a file and folder outputs; part 2: many inputs, one output, added by T5).

## Date

2026-10-20

## Context

Until v0.1 every operation took one file and wrote one file next to it, through `IOperation.RunAsync(input, ct)` and `OutputPath.WriteAtomicallyAsync`. The PDF operations of v0.2 (docs/specs/pdf.md, "Thay đổi ở core") break that shape in three ways:

1. **One file to many** (split, pages to JPG/PNG): the output is a folder of files. It must appear whole or not at all, as a single file does: Cancel or a failure must not leave a half-filled folder, and an existing folder must never be merged into.
2. **Progress inside one file**: rendering 200 pages takes long enough that "0 of 1 done" looks frozen. The window should say "page 37 of 200".
3. **Many files to one** (merge, images to PDF): part 2 below.

Every existing operation (images, dev operations) and every test calls the current contract, and other work on v0.2 runs in parallel, so the change has to leave them untouched.

## Decision (part 1)

### Folder outputs

`OutputPath.WriteDirectoryAtomicallyAsync(desiredDirectory, write, ct)` creates an empty, fixed-length `.flevert-<guid>.partial` folder beside the input, lets the operation fill it, and renames it **once** to the first free name with the same rules as a file (`name`, `name (1)`, …; a file or a folder with that name counts as taken; the name is shortened to fit 255 characters; a name taken between choosing and renaming is retried). The counter goes after the whole folder name, since a folder has no extension. `Directory.Move` never replaces anything, so an existing folder is never merged into. On an exception or cancellation the temporary folder is deleted with everything in it, by an explicit walk that removes links without following them: `Directory.Delete(recursive)` was not used because on a junction it calls `DeleteVolumeMountPoint`, which needs admin rights, and then throws with the folder left behind (found by a test).

The operation returns the folder; the runner, the log and the notification treat it like any output path (`Done: report (pages)`).

### Progress within a file

`IOperation` gains a second `RunAsync` overload as a **default interface member**:

```csharp
Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct) => RunAsync(inputPath, ct);
```

The runner always calls this overload. An operation without progress implements only the old method and inherits the default, so nothing existing changes. An operation with progress implements the new one and makes the old one call it with `null`. `FileProgress(unit, position, total)` is a validated record ("page", 1…total); the runner turns each report into a `BatchProgress` with `Within` set and `Current` naming the file, under the same lock as the start and end reports, so a page report always falls between them; a report arriving after the file finished is dropped. The progress window shows `BatchProgressText.DescribeWithin` ("page 37 of 200") on a second line, cleared when a file starts or finishes; the bar still counts files. The line's accessible name carries the text with its context ("Progress in the current file: page 37 of 200"), so UI Automation reads it. It is not a live region, so a screen reader does not announce 200 pages one by one.

`dev.split` (Dev > Split into pages, `install-menu --dev`) writes 50 page files into a folder with a page report each (`FLEVERT_DEV_PAGE_MS` per page), to exercise both parts end to end.

## Alternatives considered

- **An optional interface** (`IReportsFileProgress`) the runner tests for: works too, but adds a type test in the runner and a second place to look for what an operation can do. The default member keeps a single contract, and the runner has one call site.
- **Changing the one `RunAsync` to take progress**: breaks every operation and every test, and collides with the parallel v0.2 work, for no gain over a default member.
- **`IProgress<double>`** (the spec's first idea): a fraction cannot say "page 37 of 200", and every operation would have to agree on what 1.0 means. A unit, a position and a total cover pages, frames and images, and the window can still derive a fraction if it ever needs one.
- **An operation context object** (progress, cancellation, options in one parameter): more flexible, but a larger change than v0.2 needs; it can still come later as another default member.
- **Moving files one by one into the final folder** instead of renaming the folder once: a crash midway leaves a partial folder under its final name, which is exactly what has to be avoided.

## Consequences

- Operations that report progress must not make the two overloads call each other through the default (that would recurse); the XML documentation on `IOperation` says so.
- An operation decides how often it reports. One report per page is fine for rendering; an operation that could report thousands of times a second should report less often, since every report reaches the window's message loop.
- The runner still lets go of a cancelled file at once rather than awaiting it (unchanged). An operation that honours the token deletes its temporary folder at its next token check, while the progress window and notification keep the process alive; one stuck in a long native call when the process exits could still leave a `.partial` folder, as a `.partial` file could before.
- `BatchProgress` gained an optional `Within` parameter; existing code that constructs it is unchanged.

## Part 2: many inputs, one output

### Context

Merging PDFs and combining photos into one PDF take every selected file and write **one** output, in the order Explorer shows the names (docs/specs/pdf.md, "Thay đổi ở core" item 1). Explorer still starts one `flevert.exe` per file, in no reliable order (ADR-0002), so the batch leader is the only process that ever sees the whole selection. Requirements: nothing runs until the batch is complete; every file stays pinned for the whole run; one file that cannot be used fails the whole output, naming it, with no partial output; per-file operations behave exactly as before.

### Decision

**Contract.** The members the menu, the catalog and `list-ops` need move to `IMenuOperation` (`Id`, `MenuPath`, `Extensions`). `IOperation` extends it and is otherwise unchanged, so no existing operation or test changes. A new `IBatchOperation` extends it too:

```csharp
Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct);
```

`inputPaths` holds at least one path: the real, pinned, distinct paths in natural order. The operation writes beside `inputPaths[0]` through `OutputPath`, decides whether one file is enough (merge refuses it, combining photos accepts it), and throws `BatchInputException(inputPath, message)` to name an input it cannot use. `OperationCatalog` holds `IMenuOperation` and refuses an operation that is both kinds or neither, since `run` picks the code path by kind. The menu entry is the same per-file command as before (`run <id> "%1"`).

**Collecting the batch: no change to the leader.** `BatchLeader.ReadAllAsync` already ends only when the consumer is idle, nothing is queued, no trusted sender delivered paths for the idle window, and the batch is at least 1.5 s old; at that moment the pipe closes and the key rotates (ADR-0002). The runner for a batch operation reads that stream **to its end** before doing anything else. A consumer that only collects is idle whenever it waits, so the end of the stream is exactly "no more files will come". Only then does it pin. The idle window is at least **1 s** for a batch operation (400 ms for the others; `FLEVERT_BATCH_IDLE_MS` still widens it): a process that starts too late makes a second batch, which for a per-file operation costs a second notification but for a join costs a second, partial output. Waiting 0.6 s more before one output is cheap; the 1.5 s minimum lifetime is unchanged.

**Pin all, then sort, then run once.** Every file goes through the same checks as a per-file input (`Reject`, `InputPin.TryPin`, the extension of the real path). All pins are kept until the operation returns. Two spellings of one file (a short 8.3 name and the long one) pin the same real path and are joined once. The real paths are sorted with `NaturalOrder.PathComparer`: Windows' `StrCmpLogicalW` (shlwapi, through `LibraryImport`) on the file name, then on the whole path, then ordinal, so the order is total and the same however the files arrived. `StrCmpLogicalW` is what Explorer sorts with (the `NoStrCmpLogical` policy switches Explorer back to plain text order), so pages come in the order the user sees.

**All or nothing.** If any file is rejected, the operation is not called. Every rejected file is logged with its own reason; the single result names the first in natural order, plus how many more were rejected. A `BatchInputException` names the file in the result; any other exception fails the output with its message. The log keeps the stack trace after the first line, and the notification shows only that first line, cut to 120 characters so the log folder still fits.

**Outcome.** `BatchOutcome { OneOutput = true }` with `Total` 1 and one result (none when cancelled). Notification: `Done: <output name>`, `Failed: <file>: <reason> Details: <log folder>`, `Skipped: <reason>` or `Cancelled.`. The exit codes of core.md hold: the leader exits 0 when the output was written or skipped, 1 when it failed or was cancelled; followers exit 0 once their files are delivered. A successful join logs one line per input with the output, so the log still has every path processed.

**Progress.** One "file" for the batch line: `0 of 1 done: <first file>` at the start, the operation's reports as the second line ("file 3 of 12" for `dev.join`), `1 of 1` at the end, under one lock so no report shows after the end.

**Cancellation.** While files are still arriving, nothing runs. During the run the operation gets the token and up to 2 s to stop and delete its temporary output, then it is abandoned and the pins are released (a per-file operation is let go at once, unchanged).

`dev.join` (Dev > Join, `install-menu --dev`) concatenates `.txt` files into `<first name> (+N).txt`; one file gives `<name> (joined).txt`, since "(+0)" reads like a mistake.

### Alternatives considered

- **A "batch complete" signal or a collect phase in `BatchLeader`**: the end of `ReadAllAsync` already means that, decided by the close logic that ADR-0002's reviews hardened. A second mechanism would duplicate it and could disagree with it.
- **Starting the join with the files that are there and appending late ones**: the order is not known until the batch is complete, and a file arriving later could sort first.
- **Pinning each file only when the operation reads it**: a file could be replaced between sorting and reading, and all-or-nothing requires every file to be checked before anything is written.
- **.NET's numeric string comparison** (`CompareOptions.NumericOrdering`) instead of `StrCmpLogicalW`: managed and portable, but not guaranteed to match Explorer, which is the whole point. Matching Explorer includes its limits (below).
- **Sorting by the paths as received**: an 8.3 name (`IMG_00~1.JPG`) would sort by its short form; the real path is the name the user sees.
- **Extending `IOperation` with a list overload**: every per-file operation would carry a member that means nothing for it, and the runner could not tell the kinds apart without a flag.

### Consequences

- Followers and the menu are unchanged; the batch leader is unchanged; the per-file runner path is unchanged (its whole test suite still passes).
- **Accepted trade-off:** a process that starts more than the idle window after the last delivery still makes a second batch, and so a second output with its own `(+N)` name. A selection of more than 1000 files is split the same way (ADR-0002's `Full`), which the menu cannot reach: Explorer shows it for at most 100 files.
- **Explorer's order, with its limits** (checked on this machine): a tone or a stroke counts after letters and numbers ("Ảnh 2" sorts before "anh 3", "đ" between "d" and "e"); digit runs compare by value up to 18 digits, longer ones fall back to text order; the comparison follows the user's locale. Tests pin the behaviour that holds in every locale.
- A join holds one handle per input (at most 1000) until it finishes.
- An operation that ignores the token is abandoned after 2 s and may then read inputs that are no longer pinned, as an abandoned per-file operation can.
