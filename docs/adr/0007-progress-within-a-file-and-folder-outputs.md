# ADR-0007: Operations beyond one file in, one file out

## Status

Accepted (part 1: progress within a file and folder outputs). Part 2, many inputs to one output, is added by T5.

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
