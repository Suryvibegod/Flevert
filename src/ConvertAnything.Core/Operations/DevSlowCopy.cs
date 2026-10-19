namespace ConvertAnything.Core.Operations;

/// <summary>Like <see cref="DevCopy"/> but takes a second per file, to exercise the progress window and Cancel.</summary>
public sealed class DevSlowCopy : IOperation
{
    // CONVERTANYTHING_SLOW_COPY_MS is for integration tests that need a file to outlast the batch's idle window.
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("CONVERTANYTHING_SLOW_COPY_MS"), out var ms) ? Math.Clamp(ms, 0, 60_000) : 1000);

    private readonly DevCopy _copy = new();

    public string Id => "dev.slow-copy";

    public string MenuPath => "Dev/Slow copy";

    public IReadOnlyCollection<string> Extensions => _copy.Extensions;

    public int MaxParallel => _copy.MaxParallel;

    public async Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        await Task.Delay(Duration, ct).ConfigureAwait(false);
        return await _copy.RunAsync(inputPath, ct).ConfigureAwait(false);
    }
}
