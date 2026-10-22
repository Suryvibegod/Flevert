using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core;

/// <param name="Error">Why the file produced no output: a failure, or the reason it was skipped.</param>
/// <param name="Skipped">The operation chose not to produce an output (nothing to gain); not a failure.</param>
public sealed record FileResult(string Input, string? Output, string? Error, bool Skipped = false)
{
    public bool Succeeded => Error is null && !Skipped;

    public bool Failed => Error is not null && !Skipped;

    /// <summary>What the operation noted about the output (<see cref="OutputNote"/>), or <c>null</c>.</summary>
    public string? Note { get; init; }

    /// <summary>The input's and the output's size, when the operation reported them (<see cref="OutputNote.AddSizes"/>).</summary>
    public SizeChange? Sizes { get; init; }
}

/// <summary>How large a file was and how large its output is, in bytes.</summary>
public readonly record struct SizeChange(long OriginalBytes, long OutputBytes);

/// <summary>A file of a joining batch that never reached it (<see cref="BatchLeader.Missing"/>).</summary>
/// <param name="StillRunning">Its process was still running when the batch closed: too slow to start, rather than ended.</param>
public readonly record struct MissingFile(string Path, bool StillRunning);

/// <param name="Current">The file just started, or the file <paramref name="Within"/> is about; <c>null</c> when a file finished.</param>
/// <param name="Within">How far <paramref name="Current"/> is inside, when its operation reports it ("page 37 of 200").</param>
/// <param name="Status">What the batch is doing before any file runs, shown instead of the count ("Waiting for the
/// selected files…" while a join collects them).</param>
public sealed record BatchProgress(int Completed, int Total, string? Current, FileProgress? Within = null, string? Status = null);

/// <param name="Results">Finished files, in the order they finished (files may run in parallel). A file interrupted by
/// cancellation is not included; one that was already writing its output when cancelled may still have produced it.</param>
/// <param name="Total">Every distinct file the batch received, including those left unprocessed by cancellation.</param>
public sealed record BatchOutcome(IReadOnlyList<FileResult> Results, int Total)
{
    /// <summary>Every file was processed and none failed (skipped files are fine). Decides the exit code.</summary>
    public bool NoFailures => Results.Count == Total && Results.All(r => !r.Failed);

    /// <summary>
    /// The batch was joined into one output (<see cref="IBatchOperation"/>): <see cref="Total"/> is 1 and
    /// <see cref="Results"/> holds that output's one result, or nothing when cancelled.
    /// </summary>
    public bool OneOutput { get; init; }
}

public sealed class Runner(RunLog log)
{
    /// <summary>Called with each input's real path once it is pinned, before the operation starts: tests change the name.</summary>
    internal Action<string>? AfterPin { get; init; }

    public Task<BatchOutcome> RunAsync(
        IOperation operation, IReadOnlyList<string> files, IProgress<BatchProgress>? progress, CancellationToken ct, int maxParallel = 1)
    {
        var taken = 0;
        var source = files.Select(file => { taken++; return file; }).ToAsyncEnumerable();
        return RunAsync(operation, source, () => files.Count - taken, progress, ct, maxParallel);
    }

    /// <summary>Joins <paramref name="files"/> into one output; see the overload over a stream of files.</summary>
    public Task<BatchOutcome> RunAsync(
        IBatchOperation operation, IReadOnlyList<string> files, IProgress<BatchProgress>? progress, CancellationToken ct) =>
        RunAsync(operation, files.ToAsyncEnumerable(), progress, ct);

    /// <summary>
    /// Waits until <paramref name="files"/> ends (the batch is complete), then pins every file, sorts them in natural order
    /// and runs <paramref name="operation"/> once, holding every pin until it finishes. One file that cannot be used fails
    /// the whole output, and the result names it. ADR-0007, part 2.
    /// </summary>
    /// <param name="missing">Read once <paramref name="files"/> has ended: files of this selection that never arrived
    /// (their process ended first, <see cref="BatchLeader.Missing"/>). Any of them fails the whole output.</param>
    public async Task<BatchOutcome> RunAsync(
        IBatchOperation operation, IAsyncEnumerable<string> files, IProgress<BatchProgress>? progress, CancellationToken ct,
        Func<IReadOnlyList<MissingFile>>? missing = null)
    {
        var timer = Stopwatch.StartNew();
        var received = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Followers can take seconds to hand over their files: say so instead of an empty window.
        progress?.Report(new BatchProgress(0, 1, null, Status: CollectingStatus));
        try
        {
            // A batch leader ends this only once no more files will come (ADR-0002): the whole batch is known before
            // anything runs.
            await foreach (var file in files.WithCancellation(ct).ConfigureAwait(false))
            {
                if (seen.Add(file))
                    received.Add(file);
            }

            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Cancelled(operation, received.FirstOrDefault() ?? "(batch)", timer);
        }

        if (missing?.Invoke() is { Count: > 0 } lost)
        {
            // Joining without them would write an output that looks complete. ADR-0007, part 2. A process still running
            // was too slow to start: it did not end, and its file, if it comes, is dropped without a second notification.
            var verb = lost.All(f => f.StillRunning) ? "did not reach Flevert in time" : "never reached Flevert";
            return Failed(operation, [.. lost.Select(f => (f.Path, f.StillRunning
                    ? "did not reach Flevert in time: it arrived too late to be included"
                    : "never reached Flevert: its process ended before handing it over"))],
                timer, (name, others) => others == 0
                    ? $"{name} {verb}, so nothing was joined. Select the files again."
                    : $"{name} and {others} more {Files(others)} {verb}, so nothing was joined. Select the files again.");
        }

        if (received.Count == 0)
        {
            // Not "Done: 0 files": nothing was joined.
            var none = new FileResult("(batch)", null, "No files were received.");
            log.Write(operation.Id, none, timer.Elapsed);
            return new BatchOutcome([none], 1) { OneOutput = true };
        }

        var pins = new List<SafeFileHandle>();
        try
        {
            var inputs = new List<(string Given, string Real, SafeFileHandle? Pin)>();
            var rejected = new List<(string File, string Reason)>();
            foreach (var file in received)
            {
                SafeFileHandle? pin = null;
                var realPath = file;
                var reason = Reject(operation, file) ?? Pin(file, out pin, out realPath) ?? Unsupported(operation, realPath);
                if (pin is not null)
                    pins.Add(pin);
                if (reason is null)
                    inputs.Add((file, realPath, pin));
                else
                    rejected.Add((file, reason));
            }

            if (rejected.Count > 0)
                return Rejected(operation, rejected, timer);

            if (OperatingSystem.IsLinux())
                PinnedInput.Use(inputs.Select(i => (i.Real, i.Pin!)));
            foreach (var input in inputs)
                AfterPin?.Invoke(input.Real);

            // Two spellings of one file (a short 8.3 name and the long one) pin the same real path: join it once.
            var ordered = inputs.Select(i => (i.Given, i.Real)).DistinctBy(i => i.Real, StringComparer.Ordinal)
                .OrderBy(i => i.Real, NaturalOrder.PathComparer).ToList();
            var held = pins.ToList();
            pins.Clear(); // JoinAsync releases them
            return await JoinAsync(operation, ordered, held, progress, timer, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var pin in pins)
                pin.Dispose();
        }
    }

    /// <summary>
    /// Reports every file of <paramref name="files"/> left out of a join without opening any: they reached Flevert after
    /// their batch had closed, came from another selection while a batch was collecting, or could not reach their batch
    /// (<see cref="BatchLeader.LeavesOut"/>). One failed result names the first in natural order. ADR-0007, part 2.
    /// </summary>
    public async Task<BatchOutcome> LeaveOutAsync(IBatchOperation operation, IAsyncEnumerable<string> files, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var late = new List<string>();
        try
        {
            await foreach (var file in files.WithCancellation(ct).ConfigureAwait(false))
            {
                if (!late.Contains(file, StringComparer.Ordinal))
                    late.Add(file);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        if (late.Count == 0)
            return Cancelled(operation, "(batch)", timer);
        return Failed(operation, [.. late.Select(f => (f, "left out: it arrived after its batch had closed, or from another selection while one was being collected"))],
            timer, (name, others) => others == 0
                ? $"{name} was left out: it arrived while Flevert was joining other files. Select the files again."
                : $"{name} and {others} more {Files(others)} were left out: they arrived while Flevert was joining other files. Select the files again.");
    }

    /// <summary>
    /// Logs <paramref name="files"/> as skipped, without a notification: their batch already told the user about them
    /// (<see cref="BatchLeader.AccountedFor"/>).
    /// </summary>
    public void LogAccountedFor(IBatchOperation operation, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(files);
        foreach (var file in files)
            log.Write(operation.Id, new FileResult(file, null, AccountedForReason, Skipped: true), TimeSpan.Zero);
    }

    internal const string AccountedForReason =
        "not joined: its batch had been cancelled, or had already reported it as not reaching Flevert in time";

    /// <summary>The progress window's line while a join collects its files.</summary>
    internal const string CollectingStatus = "Waiting for the selected files…";

    private static string Files(int n) => n == 1 ? "file" : "files";

    /// <param name="message">The notification's text from the first file's name and how many others there are.</param>
    private BatchOutcome Failed(IMenuOperation operation, List<(string File, string Reason)> files, Stopwatch timer, Func<string, int, string> message)
    {
        foreach (var (file, reason) in files)
            log.Write(operation.Id, new FileResult(file, null, reason), timer.Elapsed);
        var first = files.Select(f => f.File).OrderBy(f => f, NaturalOrder.PathComparer).First();
        return new BatchOutcome([new FileResult(first, null, message(Path.GetFileName(first), files.Count - 1))], 1) { OneOutput = true };
    }

    // How long a cancelled operation may take to stop and delete its temporary output before it is abandoned: a
    // well-behaved operation stops at its next token check, well within this.
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private static async Task WaitForStopAsync(Task<string>? running)
    {
        if (running is null)
            return;
        await Task.WhenAny(running, Task.Delay(StopGrace, CancellationToken.None)).ConfigureAwait(false);
        // Also observe a failure that arrives after the grace expires and the operation has been abandoned.
        _ = running.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Held until the operation has finished, also when it was abandoned: no input can be renamed, replaced or deleted (on
    // Windows) while it is used, and it may still read through them (PinnedInput, on Linux).
    private static void Release(List<SafeFileHandle> pins, Task? running)
    {
        if (running is null || running.IsCompleted)
        {
            foreach (var pin in pins)
                pin.Dispose();
            return;
        }

        _ = running.ContinueWith(_ =>
        {
            foreach (var pin in pins)
                pin.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private async Task<BatchOutcome> JoinAsync(
        IBatchOperation operation, List<(string Given, string Real)> inputs, List<SafeFileHandle> pins, IProgress<BatchProgress>? progress,
        Stopwatch timer, CancellationToken ct)
    {
        var notes = OutputNote.Begin();
        var first = inputs[0].Given;
        var finished = false;
        var gate = new Lock();
        progress?.Report(new BatchProgress(0, 1, first));
        var within = progress is null ? null : new FileProgressRelay(p =>
        {
            lock (gate)
            {
                if (!finished)
                    progress.Report(new BatchProgress(0, 1, first, p));
            }
        });

        FileResult result;
        Task<string>? running = null;
        try
        {
            running = operation.RunAsync([.. inputs.Select(i => i.Real)], within, ct);
            result = new FileResult(first, await running.WaitAsync(ct).ConfigureAwait(false), null) { Note = notes.Text, Sizes = notes.Sizes };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await WaitForStopAsync(running).ConfigureAwait(false);
            if (running is not { IsCompletedSuccessfully: true })
            {
                lock (gate)
                    finished = true;
                return Cancelled(operation, first, timer);
            }

            // It had already written its output when it stopped: the file exists, so report and log it like any other.
            result = new FileResult(first, running.Result, null) { Note = notes.Text, Sizes = notes.Sizes };
        }
        catch (OperationSkippedException ex)
        {
            var reason = string.IsNullOrWhiteSpace(ex.Message) ? new OperationSkippedException().Message : ex.Message;
            result = new FileResult(first, null, reason, Skipped: true);
        }
        catch (BatchInputException ex) when (ex.InputPath is not null)
        {
            // The operation names the real path it was given; report the file as the batch received it.
            var culprit = inputs.Where(i => i.Real == ex.InputPath).Select(i => i.Given).FirstOrDefault() ?? ex.InputPath;
            result = new FileResult(culprit, null, PinnedInput.ShowNames($"{Path.GetFileName(culprit)}: {ex.Message}{Environment.NewLine}{ex}"));
        }
#pragma warning disable CA1031 // Whatever the operation throws, the batch ends with a result and a notification.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = new FileResult(first, null, PinnedInput.ShowNames($"{ex.Message}{Environment.NewLine}{ex}"));
        }
        finally
        {
            Release(pins, running);
        }

        lock (gate)
            finished = true;

        if (result.Succeeded)
        {
            foreach (var (given, _) in inputs)
                log.Write(operation.Id, result with { Input = given }, timer.Elapsed);
        }
        else
        {
            log.Write(operation.Id, result, timer.Elapsed);
        }

        progress?.Report(new BatchProgress(1, 1, null));
        return new BatchOutcome([result], 1) { OneOutput = true };
    }

    private BatchOutcome Rejected(IMenuOperation operation, List<(string File, string Reason)> rejected, Stopwatch timer)
    {
        foreach (var (file, reason) in rejected)
            log.Write(operation.Id, new FileResult(file, null, reason), timer.Elapsed);

        // Named in the order the user sees the files, so the same selection always names the same file.
        var (named, why) = rejected.OrderBy(r => r.File, NaturalOrder.PathComparer).First();
        var others = rejected.Count - 1;
        var error = $"{Path.GetFileName(named)}: {why}" + others switch
        {
            0 => "",
            1 => " 1 more file could not be used either.",
            _ => $" {others} more files could not be used either.",
        };
        return new BatchOutcome([new FileResult(named, null, error)], 1) { OneOutput = true };
    }

    private BatchOutcome Cancelled(IMenuOperation operation, string file, Stopwatch timer)
    {
        log.Write(operation.Id, new FileResult(file, null, "cancelled"), timer.Elapsed);
        return new BatchOutcome([], 1) { OneOutput = true };
    }

    /// <param name="pending">How many received files are still queued; lets progress grow as late files join the batch.</param>
    /// <param name="maxParallel">How many files run at once. Image decoders use one core each, so a batch gains from
    /// running several (docs/specs/core.md, "Xử lý song song trong một lô"); results then come in the order files finish.</param>
    /// <param name="running">Told how many files are running: raised before the next file is requested, lowered after
    /// a result is recorded, so a batch leader never sees the batch idle while a file is still being converted. Required
    /// with a <see cref="BatchLeader"/> as the source and <paramref name="maxParallel"/> above 1 (pass
    /// <see cref="BatchLeader.ReportRunning"/>), or the batch can close early and a late file start a second batch.</param>
    public async Task<BatchOutcome> RunAsync(
        IOperation operation,
        IAsyncEnumerable<string> files,
        Func<int> pending,
        IProgress<BatchProgress>? progress,
        CancellationToken ct,
        int maxParallel = 1,
        Action<int>? running = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallel, 1);
        var results = new List<FileResult>();
        // Exact comparison: case-sensitive folders can hold a.txt and A.txt as different files.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var interrupted = 0;
        var runningCount = 0;
        var inFlight = new List<Task>();
        var gate = new Lock();

        // Results, counts and progress change together under one lock, so the running count only ever moves in order.
        // The total can dip by one for a moment: the queue hands out a file before it is counted as running. The
        // callbacks only record or post the numbers.
        // A report inside a file goes out under the same lock, so it always falls between the file's start and its end;
        // one that comes after the end (an operation that reports late) is dropped rather than shown for a finished file.
        async Task RunAndRecordAsync(string file)
        {
            var finished = false;
            var within = progress is null ? null : new FileProgressRelay(p =>
            {
                lock (gate)
                {
                    if (!finished)
                        progress.Report(new BatchProgress(results.Count, results.Count + runningCount + pending(), file, p));
                }
            });
            var result = await RunOneAsync(operation, file, within, ct).ConfigureAwait(false);
            lock (gate)
            {
                finished = true;
                if (result is null)
                    interrupted++;
                else
                    results.Add(result);
                running?.Invoke(--runningCount);
                progress?.Report(new BatchProgress(results.Count, results.Count + runningCount + pending(), null));
            }
        }

        try
        {
            await foreach (var file in files.WithCancellation(ct).ConfigureAwait(false))
            {
                if (!seen.Add(file))
                    continue;

                lock (gate)
                {
                    running?.Invoke(++runningCount);
                    progress?.Report(new BatchProgress(results.Count, results.Count + runningCount + pending(), file));
                }

                inFlight.Add(RunAndRecordAsync(file));
                if (inFlight.Count >= maxParallel)
                {
                    var done = await Task.WhenAny(inFlight).ConfigureAwait(false);
                    inFlight.Remove(done);
                    await done.ConfigureAwait(false); // surfaces a fault instead of dropping it
                }

                if (ct.IsCancellationRequested)
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            // Files already started finish (or stop at the cancel) before the outcome is known, also when the source
            // failed: the batch must not report and exit while files are still being written.
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }

        return new BatchOutcome(results, results.Count + interrupted + pending());
    }

    /// <returns>The result, or <c>null</c> when cancellation interrupted this file.</returns>
    private async Task<FileResult?> RunOneAsync(IOperation operation, string file, IProgress<FileProgress>? within, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var notes = OutputNote.Begin(); // this file's own, also when files run in parallel
        SafeFileHandle? pin = null;
        FileResult result;
        Task<string>? running = null;
        try
        {
            var realPath = file;
            var reason = Reject(operation, file) ?? Pin(file, out pin, out realPath) ?? Unsupported(operation, realPath);
            if (reason is null && OperatingSystem.IsLinux())
                PinnedInput.Use([(realPath, pin!)]);
            if (reason is null)
                AfterPin?.Invoke(realPath);
            // Keep the task so cancellation can wait for its child processes and temporary output to be cleaned up.
            result = reason is not null
                ? new FileResult(file, null, reason)
                : new FileResult(file, await (running = operation.RunAsync(realPath, within, ct)).WaitAsync(ct).ConfigureAwait(false), null) { Note = notes.Text, Sizes = notes.Sizes };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await WaitForStopAsync(running).ConfigureAwait(false);
            if (running is not { IsCompletedSuccessfully: true })
            {
                log.Write(operation.Id, new FileResult(file, null, "cancelled"), timer.Elapsed);
                return null;
            }

            // An atomic commit may have won the race with cancellation: report the output that now exists.
            result = new FileResult(file, running.Result, null) { Note = notes.Text, Sizes = notes.Sizes };
        }
        catch (OperationSkippedException ex)
        {
            var reason = string.IsNullOrWhiteSpace(ex.Message) ? new OperationSkippedException().Message : ex.Message;
            result = new FileResult(file, null, reason, Skipped: true);
        }
#pragma warning disable CA1031 // One bad file must never stop the rest of the batch.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The message on the first line for the notification, as a join does; the stack for the log after it. A
            // pinned input read through /proc/<flevert>/fd/<n> is named as the user knows it.
            result = new FileResult(file, null, PinnedInput.ShowNames($"{ex.Message}{Environment.NewLine}{ex}"));
        }
        finally
        {
            if (pin is not null)
                Release([pin], running);
        }

        log.Write(operation.Id, result, timer.Elapsed);
        return result;
    }

    // Nothing here opens or probes the path: the pin is the first and only open of the file, with SQOS and without
    // following links.
    private static string? Reject(IMenuOperation operation, string file)
    {
        // On Linux a name may end in a dot or a space or hold a colon, and no 8.3 names exist: only the shape is checked.
        if (!OperatingSystem.IsWindows())
            return IsPlainUnixPath(file) ? Unsupported(operation, file) : "Only absolute file paths are accepted.";

        // Paths also arrive over the batch pipe; accept only plain absolute file paths, not device namespaces. flevert.exe
        // keeps a relative path with a dotted name unresolved, so say what is actually wrong with it.
        if (!IsPlainFilePath(file))
        {
            return !Path.IsPathFullyQualified(file) && InputPath.HasNameEndingInDotOrSpace(file)
                ? InputPath.NameEndsInDotOrSpaceMessage
                : "Only absolute file paths are accepted.";
        }

        if (InputPath.HasStreamName(file))
            return InputPath.StreamNameMessage;
        if (InputPath.HasNameEndingInDotOrSpace(file))
            return InputPath.NameEndsInDotOrSpaceMessage;
        // A short 8.3 name cuts a 4-letter extension (.webp -> .WEB); its type is judged on the real name after pinning.
        return Path.GetFileName(file).Contains('~', StringComparison.Ordinal) ? null : Unsupported(operation, file);
    }

    // Checked again on the real path after pinning: a short 8.3 name that could not be expanded ends in a truncated
    // extension (X~1.TXT for x.txtq).
    private static string? Unsupported(IMenuOperation operation, string path)
    {
        var extension = Path.GetExtension(path);
        return operation.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"File type {extension} is not supported by {operation.Id}.";
    }

    private static bool IsPlainUnixPath(string file) => file.StartsWith('/') && !file.Contains('\0', StringComparison.Ordinal);

    /// <summary>
    /// Allow-list, either slash: <c>C:\…</c>, <c>\\server\share\…</c>, <c>\\?\C:\…</c> or <c>\\?\UNC\…</c>. The share may not
    /// be <c>pipe</c> or <c>IPC$</c>: opening a named pipe as a file lets its server impersonate the user.
    /// </summary>
    private static bool IsPlainFilePath(string file)
    {
        if (!Path.IsPathFullyQualified(file))
            return false;

        var p = file.Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return IsFileShare(p[@"\\?\UNC\".Length..]);
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            return p.Length > 6 && char.IsAsciiLetter(p[4]) && p[5] == ':' && p[6] == '\\';
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
            return p.Length > 2 && p[2] is not ('.' or '?') && IsFileShare(p[2..]);
        return p.Length > 2 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
    }

    /// <param name="serverAndShare"><c>server\share\…</c>. A server starting with <c>;</c> is a redirector's own name
    /// (<c>\\;LanmanRedirector\host\pipe\…</c>), which shifts the real share one place along.</param>
    private static bool IsFileShare(string serverAndShare)
    {
        var parts = serverAndShare.Split('\\');
        return parts.Length > 2 && parts[0] is { Length: > 0 } server && server[0] != ';' && parts[1] is { Length: > 0 } share
            && !share.Equals("pipe", StringComparison.OrdinalIgnoreCase)
            && !share.Equals("IPC$", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Pin(string file, out SafeFileHandle? pin, out string realPath)
    {
        // Linux has no mandatory locks: the pin keeps the file open and checks it, but cannot stop a rename (ADR-0010).
        string? reason;
        if (OperatingSystem.IsWindows())
            pin = InputPin.TryPin(file, out reason, out realPath);
        else if (OperatingSystem.IsLinux())
            pin = UnixInputPin.TryPin(file, out reason, out realPath);
        else
            throw new PlatformNotSupportedException();
        return reason;
    }

    private sealed class FileProgressRelay(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }
}
