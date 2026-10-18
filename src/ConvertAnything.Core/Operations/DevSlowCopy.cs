namespace ConvertAnything.Core.Operations;

/// <summary>Like <see cref="DevCopy"/> but takes a second per file, to exercise the progress window and Cancel.</summary>
public sealed class DevSlowCopy : IOperation
{
    private readonly DevCopy _copy = new();

    public string Id => "dev.slow-copy";

    public string MenuPath => "Dev/Slow copy";

    public IReadOnlyCollection<string> Extensions => _copy.Extensions;

    public async Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        return await _copy.RunAsync(inputPath, ct).ConfigureAwait(false);
    }
}
