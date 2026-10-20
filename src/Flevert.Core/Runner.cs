using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core;

/// <param name="Error">Why the file produced no output: a failure, or the reason it was skipped.</param>
/// <param name="Skipped">The operation chose not to produce an output (nothing to gain); not a failure.</param>
public sealed record FileResult(string Input, string? Output, string? Error, bool Skipped = false)
{
    public bool Succeeded => Error is null && !Skipped;

    public bool Failed => Error is not null && !Skipped;
}

/// <param name="Current">The file just started, or the file <paramref name="Within"/> is about; <c>null</c> when a file finished.</param>
/// <param name="Within">How far <paramref name="Current"/> is inside, when its operation reports it ("page 37 of 200").</param>
public sealed record BatchProgress(int Completed, int Total, string? Current, FileProgress? Within = null);

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
    public async Task<BatchOutcome> RunAsync(
        IBatchOperation operation, IAsyncEnumerable<string> files, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var received = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
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

        if (received.Count == 0)
            return new BatchOutcome([], 0) { OneOutput = true };

        var pins = new List<SafeFileHandle>();
        try
        {
            var inputs = new List<(string Given, string Real)>();
            var rejected = new List<(string File, string Reason)>();
            foreach (var file in received)
            {
                SafeFileHandle? pin = null;
                var realPath = file;
                var reason = Reject(operation, file) ?? Pin(file, out pin, out realPath) ?? Unsupported(operation, realPath);
                if (pin is not null)
                    pins.Add(pin);
                if (reason is null)
                    inputs.Add((file, realPath));
                else
                    rejected.Add((file, reason));
            }

            if (rejected.Count > 0)
                return Rejected(operation, rejected, timer);

            // Two spellings of one file (a short 8.3 name and the long one) pin the same real path: join it once.
            var ordered = inputs.DistinctBy(i => i.Real, StringComparer.Ordinal).OrderBy(i => i.Real, NaturalOrder.PathComparer).ToList();
            return await JoinAsync(operation, ordered, progress, timer, ct).ConfigureAwait(false);
        }
        finally
        {
            // Held until the operation has finished (or been abandoned): no input can be renamed, replaced or deleted while
            // it is being joined.
            foreach (var pin in pins)
                pin.Dispose();
        }
    }

    // How long a cancelled join may take to stop and delete its temporary output before it is abandoned (and its inputs
    // unpinned): a well-behaved operation stops at its next token check, well within this.
    private static readonly TimeSpan JoinStopGrace = TimeSpan.FromSeconds(2);

    private async Task<BatchOutcome> JoinAsync(
        IBatchOperation operation, List<(string Given, string Real)> inputs, IProgress<BatchProgress>? progress, Stopwatch timer,
        CancellationToken ct)
    {
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
            result = new FileResult(first, await running.WaitAsync(ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (running is not null)
                await Task.WhenAny(running, Task.Delay(JoinStopGrace, CancellationToken.None)).ConfigureAwait(false);
            lock (gate)
                finished = true;
            return Cancelled(operation, first, timer);
        }
        catch (OperationSkippedException ex)
        {
            var reason = string.IsNullOrWhiteSpace(ex.Message) ? new OperationSkippedException().Message : ex.Message;
            result = new FileResult(first, null, reason, Skipped: true);
        }
        catch (BatchInputException ex)
        {
            // The operation names the real path it was given; report the file as the batch received it.
            var culprit = inputs.Where(i => i.Real == ex.InputPath).Select(i => i.Given).FirstOrDefault() ?? ex.InputPath ?? first;
            result = new FileResult(culprit, null, $"{Path.GetFileName(culprit)}: {ex.Message}{Environment.NewLine}{ex}");
        }
#pragma warning disable CA1031 // Whatever the operation throws, the batch ends with a result and a notification.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = new FileResult(first, null, $"{ex.Message}{Environment.NewLine}{ex}");
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
        SafeFileHandle? pin = null;
        FileResult result;
        try
        {
            var realPath = file;
            var reason = Reject(operation, file) ?? Pin(file, out pin, out realPath) ?? Unsupported(operation, realPath);
            // WaitAsync: an operation that ignores the token must not keep a cancelled batch alive; it is abandoned.
            result = reason is not null
                ? new FileResult(file, null, reason)
                : new FileResult(file, await operation.RunAsync(realPath, within, ct).WaitAsync(ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Write(operation.Id, new FileResult(file, null, "cancelled"), timer.Elapsed);
            return null;
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
            result = new FileResult(file, null, ex.ToString());
        }
        finally
        {
            pin?.Dispose();
        }

        log.Write(operation.Id, result, timer.Elapsed);
        return result;
    }

    // Nothing here opens or probes the path: the pin is the first and only open of the file, with SQOS and without
    // following links.
    private static string? Reject(IMenuOperation operation, string file)
    {
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
        pin = InputPin.TryPin(file, out var reason, out realPath);
        return reason;
    }

    private sealed class FileProgressRelay(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }
}
