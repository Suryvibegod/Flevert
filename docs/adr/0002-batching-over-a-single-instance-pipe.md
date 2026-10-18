# ADR-0002: Batch selected files over a single-instance named pipe

## Status

Accepted

## Date

2026-10-19 (revised the same day after the second and third adversarial reviews)

## Context

Command-line context menu verbs (ADR-0001) make Explorer start one `ca.exe` per selected file, up to 100 processes within a second or two. Users expect one progress window and one notification per selection, not one per file. Requirements:

- Exactly one process handles the whole selection; no selected file may be lost. A duplicate output is a defect but less severe than a lost file.
- Different operations, users and sessions never share a batch.
- Every process ends in bounded time, even if the leader crashes or hangs, or another account interferes.
- The first file should start immediately; slow-starting processes must not split the selection.

## Decision

- **Leader election by pipe creation.** Each process tries to create `NamedPipeServerStream` with `maxNumberOfServerInstances: 1` and `CurrentUserOnly`. The one that succeeds leads; the rest connect as clients.
- **Streaming batch.** The leader starts processing its own file at once and keeps accepting paths while it works. It closes the pipe only when the consumer is idle, nothing is queued, no connection arrived for 400 ms, and the batch is at least 1.5 s old (so slow-starting processes do not split a fast batch). When the consumer stops early (Cancel, error) the leader stops accepting immediately.
- **Hand-over protocol.** The client sends length-prefixed raw UTF-16 paths. The leader queues them (deduplicated, ordinal) and replies `Accepted`, then waits for the client to close before resetting the pipe (Disconnect discards unread data). If the message would push the batch past 1000 files it replies `Full` and the sender processes its files itself; the leader's own files are never capped. Every connection has a 2 s timeout, and a client that breaks the pipe is disconnected without ending the batch.
- **Name and key.** The pipe name contains the user SID, the session id, a per-operation random key and the op id. Keys live in `%LOCALAPPDATA%\ConvertAnything\pipes`, a folder whose ACL is limited to the user, SYSTEM and Administrators and labelled medium no-read-up. Keys are created atomically (first writer wins), regenerated when corrupt, and **rotated when a batch closes**, because pipe names are visible to every account in the pipe namespace.
- **Peer checks.** Both ends require the peer to run the same executable, in the same session, at an integrity level no lower than their own.
- **Robust listener.** A connection wait that faults (a client vanished before the connection completed) or a failed Disconnect replaces the pipe instance; idleness is measured from the last connection. Untrusted clients get a `Refused` reply rather than silence. The key is rotated before the pipe name is released, and only if it is still the key this batch used; the name is recomputed on every join attempt, and a refused or foreign-owned name rotates the key and retries once.
- **Fallbacks.** A pipe name held by another account, an untrusted peer, a `Full` reply, a failure to set up the key, or a 10 s overall join deadline makes the process work alone.

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

- Verified by tests: 50 concurrent in-process callers and 20 real `ca.exe` processes each form one batch; a process started while the leader is busy joins its batch; a client that disconnects without sending neither ends nor breaks the batch; an overflowing message is refused so its sender works alone; corrupt keys are regenerated; paths with lone surrogates survive.
- A follower costs about 87 ms start-to-exit (median, `scripts/measure-startup.ps1`).
- **Accepted trade-off:** if the leader crashes after acknowledging followers, their files are not processed (followers have already exited). The user sees no notification and can retry. Logging can no longer crash the leader (it is best effort), which removes the known cause.
- **Accepted trade-off:** a duplicate is possible across batches if an ack is lost and the retry becomes a new leader after the first batch closed; the output is then named `x (1)`.
- Files that arrive after the user presses Cancel are no longer acknowledged: the leader stops accepting as soon as its consumer stops.
- Two selections of the same operation made while a batch is running merge into that batch.
- **Accepted trade-off:** a new selection started in the instant between a batch closing and its key rotating can split into two batches.
- **Accepted trade-off:** outputs take the ACL of the folder they are written to, not of the input file, so a file the user protected with its own explicit ACL produces a copy readable by whoever can read that folder.
- **Accepted trade-off:** an operation that ignores cancellation is abandoned; if the process exits before it finishes, its hidden `.ca-*.partial` file can be left behind.
