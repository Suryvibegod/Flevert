using System.Diagnostics;

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
        return RunAsync(operation, Enumerate(), () => files.Count - taken, progress, ct);

        async IAsyncEnumerable<string> Enumerate()
        {
            foreach (var file in files)
            {
                taken++;
                yield return file;
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
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
        FileResult result;
        try
        {
            if (!File.Exists(file))
                throw new FileNotFoundException("Input file not found.", file);

            result = new FileResult(file, await operation.RunAsync(file, ct).ConfigureAwait(false), null);
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

        log.Write(operation.Id, result, timer.Elapsed);
        return result;
    }
}
