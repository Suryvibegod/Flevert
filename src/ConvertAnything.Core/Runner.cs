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
            var reason = Reject(operation, file) ?? Pin(file, out pin, out realPath) ?? Unsupported(operation, realPath);
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
        if (!IsPlainFilePath(file))
            return "Only absolute file paths are accepted.";
        // Win32 drops a trailing dot or space, so such a name would be read as a different file than the one selected.
        if (file.EndsWith('.') || file.EndsWith(' '))
            return "Names ending in a dot or space are not supported.";

        var info = new FileInfo(file);
        if (!info.Exists)
            return "Input file not found.";
        return Unsupported(operation, file);
    }

    // Checked again on the real path after pinning: a short 8.3 name that could not be expanded ends in a truncated
    // extension (X~1.TXT for x.txtq).
    private static string? Unsupported(IOperation operation, string path)
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

    /// <param name="serverAndShare"><c>server\share\…</c></param>
    private static bool IsFileShare(string serverAndShare)
    {
        var parts = serverAndShare.Split('\\');
        return parts.Length > 2 && parts[1] is { Length: > 0 } share
            && !share.Equals("pipe", StringComparison.OrdinalIgnoreCase)
            && !share.Equals("IPC$", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Pin(string file, out SafeFileHandle? pin, out string realPath)
    {
        pin = InputPin.TryPin(file, out var reason, out realPath);
        return reason;
    }
}
