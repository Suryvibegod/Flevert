using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace ConvertAnything.Core;

public sealed record FileResult(string Input, string? Output, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record BatchProgress(int Completed, int Total, string? Current);

/// <param name="Results">Finished files, in processing order. A file interrupted by cancellation is not included.</param>
/// <param name="Total">Every distinct file the batch received, including those left unprocessed by cancellation.</param>
public sealed record BatchOutcome(IReadOnlyList<FileResult> Results, int Total);

public sealed class Runner(RunLog log)
{
    public Task<BatchOutcome> RunAsync(
        IOperation operation, IReadOnlyList<string> files, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var taken = 0;
        var source = files.Select(file => { taken++; return file; }).ToAsyncEnumerable();
        return RunAsync(operation, source, () => files.Count - taken, progress, ct);
    }

    /// <param name="pending">How many received files are still queued; lets progress grow as late files join the batch.</param>
    public async Task<BatchOutcome> RunAsync(
        IOperation operation,
        IAsyncEnumerable<string> files,
        Func<int> pending,
        IProgress<BatchProgress>? progress,
        CancellationToken ct)
    {
        var results = new List<FileResult>();
        // Exact comparison: case-sensitive folders can hold a.txt and A.txt as different files.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var interrupted = 0;

        try
        {
            await foreach (var file in files.WithCancellation(ct).ConfigureAwait(false))
            {
                if (!seen.Add(file))
                    continue;

                progress?.Report(new BatchProgress(results.Count, results.Count + 1 + pending(), file));
                var result = await RunOneAsync(operation, file, ct).ConfigureAwait(false);
                if (result is null)
                {
                    interrupted = 1;
                    break;
                }

                results.Add(result);
                progress?.Report(new BatchProgress(results.Count, results.Count + pending(), null));
                if (ct.IsCancellationRequested)
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        return new BatchOutcome(results, results.Count + interrupted + pending());
    }

    /// <returns>The result, or <c>null</c> when cancellation interrupted this file.</returns>
    private async Task<FileResult?> RunOneAsync(IOperation operation, string file, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        SafeFileHandle? pin = null;
        FileResult result;
        try
        {
            var realPath = file;
            var reason = Reject(operation, file) ?? Pin(file, out pin, out realPath);
            // WaitAsync: an operation that ignores the token must not keep a cancelled batch alive; it is abandoned.
            result = reason is not null
                ? new FileResult(file, null, reason)
                : new FileResult(file, await operation.RunAsync(realPath, ct).WaitAsync(ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Write(operation.Id, new FileResult(file, null, "cancelled"), timer.Elapsed);
            return null;
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

    private static string? Reject(IOperation operation, string file)
    {
        // Paths also arrive over the batch pipe; accept only plain absolute file paths, not device namespaces.
        if (!Path.IsPathFullyQualified(file)
            || file.StartsWith(@"\\.\", StringComparison.Ordinal)
            || file.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            return "Only absolute file paths are accepted.";

        var info = new FileInfo(file);
        if (!info.Exists)
            return "Input file not found.";
        if (!operation.Extensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase))
            return $"File type {info.Extension} is not supported by {operation.Id}.";
        return null;
    }

    private static string? Pin(string file, out SafeFileHandle? pin, out string realPath)
    {
        pin = InputPin.TryPin(file, out var reason, out realPath);
        return reason;
    }
}
