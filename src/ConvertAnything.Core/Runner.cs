using System.Diagnostics;

namespace ConvertAnything.Core;

public sealed record FileResult(string Input, string? Output, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record BatchProgress(int Completed, int Total, string? Current);

public sealed class Runner(RunLog log)
{
    public async Task<IReadOnlyList<FileResult>> RunAsync(
        IOperation operation,
        IReadOnlyList<string> files,
        IProgress<BatchProgress>? progress,
        CancellationToken ct)
    {
        var results = new List<FileResult>(files.Count);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested)
                break;

            progress?.Report(new BatchProgress(results.Count, files.Count, file));
            var result = await RunOneAsync(operation, file, ct).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(new BatchProgress(results.Count, files.Count, null));
        }

        return results;
    }

    private async Task<FileResult> RunOneAsync(IOperation operation, string file, CancellationToken ct)
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
            result = new FileResult(file, null, "cancelled");
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
