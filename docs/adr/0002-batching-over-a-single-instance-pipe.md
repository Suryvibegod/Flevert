# ADR-0002: Batch selected files over a single-instance named pipe

## Status

Accepted

## Date

2026-10-19 (revised the same day after the second, third and fourth adversarial reviews)

## Context

Command-line context menu verbs (ADR-0001) make Explorer start one `flevert.exe` per selected file, up to 100 processes within a second or two. Users expect one progress window and one notification per selection, not one per file. Requirements:

- Exactly one process handles the whole selection; no selected file may be lost. A duplicate output is a defect but less severe than a lost file.
- Different operations, users and sessions never share a batch.
- Every process ends in bounded time, even if the leader crashes or hangs, or another account interferes.
- The first file should start immediately; slow-starting processes must not split the selection.

## Decision

- **Leader election by pipe creation.** Each process tries to create `NamedPipeServerStream` with `maxNumberOfServerInstances: 1` and `CurrentUserOnly`. The one that succeeds leads; the rest connect as clients.
- **Streaming batch.** The leader starts processing its own file at once and keeps accepting paths while it works. It closes the pipe only when the consumer is idle, nothing is queued, no file is still running, no connection arrived for 400 ms, and the batch is at least 1.5 s old (so slow-starting processes do not split a fast batch). When the consumer stops early (Cancel, error) the leader stops accepting immediately.
- **Several files at once (2026-10-19).** The leader converts up to `min(operation's MaxParallel, cores − 1, 8)` files at a time: ImageMagick here has no OpenMP, so an image uses one core (libheif adds a few threads for HEIC tiles), and a batch of ten 12 MP photos went from 23.9 s to 6.9 s under the same load. Operations run one file at a time unless they opt in (image and dev operations do; ffmpeg in v0.2 already uses every core). The runner tells the leader how many files are running before it asks for the next one and after it records each result, because with files in parallel the consumer asks for more while earlier files still run, and the leader must not see that as idle. Image operations share ImageMagick's memory limit through a budget (docs/specs/image.md), and a leader running files in parallel lowers its priority to below normal. Results come in the order files finish, so when two inputs map to the same output name, which one gets `name (1)` depends on that order.
- **Hand-over protocol.** The client sends length-prefixed raw UTF-16 paths. The leader queues them (deduplicated, ordinal) and replies `Accepted`, then waits for the client to close before resetting the pipe. The pipe is unbuffered, so a reply write completes only once the client has read it; Disconnect, which discards unread data, can never drop a reply (a test fails if buffers are added). If the message would push the batch past 1000 files it replies `Full`; the sender then rotates the key and leads a new batch, which the rest of a very large selection joins. The leader's own files are never capped. Every connection has a 2 s timeout, and a client that breaks the pipe is disconnected without ending the batch.
- **Name and key.** The pipe name contains the user SID, the session id, a per-operation random key and the op id. Keys live in `%LOCALAPPDATA%\Flevert\pipes`, a folder whose ACL is limited to the user, SYSTEM and Administrators and labelled medium no-read-up. Keys are created atomically (first writer wins), regenerated when corrupt, and **rotated when a batch closes**, because pipe names are visible to every account in the pipe namespace.
- **Peer checks.** Both ends require the peer to run the same executable (compared by the image path the kernel reports for both processes, so a launch through a junction or `subst` drive still matches), in the same session, at an integrity level no lower than their own.
- **Robust listener.** A connection wait that faults (a client vanished before the connection completed) or a failed Disconnect replaces the pipe instance. Idleness is measured from the end of the last exchange in which a trusted sender delivered paths, and the close condition is checked after every connection, so clients that connect without sending cannot hold a batch open. If the instance cannot be recreated, the name was taken while it was free (possibly by a newer batch with the same key), so the batch ends without rotating the key. Untrusted clients get a `Refused` reply rather than silence. The key is rotated before the pipe name is released, and only if it is still the key this batch used (rotation is best effort and can never stop the batch from closing); the name is recomputed on every join attempt, and a refused or foreign-owned name rotates the key and retries once.
- **Fallbacks.** A pipe name held by another account or an untrusted peer (after one key rotation), a failure to set up the key, or a 10 s overall join deadline makes the process work alone. Once connected, a follower keeps the full 2 s for the exchange even near that deadline, so a late ack is not lost.

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

- Verified by tests: 50 concurrent in-process callers and 20 real `flevert.exe` processes each form one batch; a process started while the leader is busy joins its batch; a client that disconnects without sending neither ends nor breaks the batch, and clients that keep connecting without sending cannot hold it open; an overflowing message is refused so its sender leads a batch of its own; real `flevert.exe` processes started through a junction still form one batch; closing work that throws, or a lost pipe name, still ends the batch (the latter without rotating the key); corrupt keys are regenerated; paths with lone surrogates survive.
- A follower costs about 87 ms start-to-exit (median, `scripts/measure-startup.ps1`).
- **Accepted trade-off:** if the leader crashes after acknowledging followers, their files are not processed (followers have already exited). The user sees no notification and can retry. Logging can no longer crash the leader (it is best effort), which removes the known cause.
- **Accepted trade-off:** a duplicate is possible across batches if an ack is lost and the retry becomes a new leader after the first batch closed; the output is then named `x (1)`.
- Files that arrive after the user presses Cancel are no longer acknowledged: the leader stops accepting as soon as its consumer stops.
- Two selections of the same operation made while a batch is running merge into that batch.
- **Accepted trade-off:** a new selection started in the instant between a batch closing and its key rotating can split into two batches.
- **Accepted trade-off:** under heavy load a legitimate follower can split a batch: its exchange can outlast the leader's 2 s budget (so the idle timer is not restarted and an idle leader closes), or it can wait out its connect timeout behind a silent client. Its files then form a second batch; none are lost or processed twice.
- An untrusted sender's request is read (and dropped) before it is refused: senders write before they read, and on the unbuffered pipe a reply written first would leave both sides waiting until the timeout.
- **Accepted trade-off:** outputs take the ACL of the folder they are written to, not of the input file, so a file the user protected with its own explicit ACL produces a copy readable by whoever can read that folder.
- **Accepted trade-off:** an operation that ignores cancellation is abandoned; if the process exits before it finishes, its hidden `.ca-*.partial` file can be left behind.
