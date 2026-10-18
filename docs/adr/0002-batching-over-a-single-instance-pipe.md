# ADR-0002: Batch selected files over a single-instance named pipe

## Status

Accepted

## Date

2026-10-19

## Context

Command-line context menu verbs (ADR-0001) make Explorer start one `ca.exe` per selected file, up to 100 processes within a second or two. Users expect one progress window and one notification per selection, not one per file. Requirements:

- Exactly one process handles the whole selection; no selected file may be lost. A duplicate output is a defect but less severe than a lost file.
- Different operations, users and sessions never share a batch.
- Every process ends in bounded time, even if the leader crashes or hangs, or another account interferes.
- The first file should start immediately; slow-starting processes must not split the selection.

## Decision

- **Leader election by pipe creation.** Each process tries to create `NamedPipeServerStream` with `maxNumberOfServerInstances: 1` and `CurrentUserOnly`. The one that succeeds leads; the rest connect as clients.
- **Streaming batch.** The leader starts processing its own file at once and keeps accepting paths while it works. It closes the pipe only when the consumer is idle, nothing is queued, and no connection arrived for 400 ms. Late files join the running batch and the progress total grows.
- **Hand-over protocol.** Client sends length-prefixed raw UTF-16 paths. The leader queues them, writes a one-byte ack, flushes, and waits for the pipe to drain before disconnecting (Disconnect discards unread data). Each connection has a 2 s timeout. The runner drops exact (ordinal) duplicates, so a lost ack and a retry never produce two outputs within a batch.
- **Name and peer checks.** The pipe name contains the user SID, the session id, a 16-character random secret stored in `%LOCALAPPDATA%\ConvertAnything\pipe.key` and the op id. Both ends check, via `GetNamedPipe{Server,Client}ProcessId` and the process image path, that the peer runs the same executable.
- **Fallbacks.** A pipe name held by another account (`UnauthorizedAccessException`) or an untrusted peer makes the process work alone immediately. After 20 failed attempts a process also works alone. At most 1000 files per batch.

## Alternatives Considered

### Fixed collection window, then process (first design)

- Pros: simplest.
- Cons: adds the window to every click, and a process that starts after the window, which is likely with 100 cold starts on a loaded machine, became a second leader with a second UI. Found in adversarial review.
- Rejected.

### Named mutex plus pipe

- Pros: conventional single-instance pattern.
- Cons: mutex ownership is thread-affine, which fights async code; the single-instance pipe already provides mutual exclusion and is released by the OS if the leader dies.
- Rejected.

### Spool files on disk

- Pros: survives a leader crash, since followers persist their path before exiting.
- Cons: more moving parts (cleanup, locking, stale files) for a rare failure.
- Rejected for now; see consequences.

### COM verb (DropTarget) that receives the whole selection in one call

- Pros: no coordination at all, no 100-file limit.
- Cons: COM registration and out-of-process server; larger change.
- Deferred; revisit with the Windows 11 modern menu work.

## Consequences

- Verified by tests: 50 concurrent in-process callers and 20 real `ca.exe` processes each form one batch; a process started while the leader is busy joins its batch; paths with lone surrogates survive.
- A follower costs about 87 ms start-to-exit (median, `scripts/measure-startup.ps1`).
- **Accepted trade-off:** if the leader crashes after acknowledging followers, their files are not processed (followers have already exited). The user sees no notification and can retry. Logging can no longer crash the leader (it is best effort), which removes the known cause.
- **Accepted trade-off:** a duplicate is possible across batches if an ack is lost and the retry becomes a new leader after the first batch closed; the output is then named `x (1)`.
- **Accepted trade-off:** files that arrive after the user presses Cancel are acknowledged but not processed.
- Two selections of the same operation made while a batch is running merge into that batch.
