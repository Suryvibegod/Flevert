using ConvertAnything.Core;

namespace ConvertAnything.Image;

/// <summary>Runs an image operation once its share of the <see cref="MemoryBudget"/> is free.</summary>
public sealed class BudgetedOperation(IOperation inner, MemoryBudget? budget = null) : IOperation
{
    public string Id => inner.Id;

    public string MenuPath => inner.MenuPath;

    public IReadOnlyCollection<string> Extensions => inner.Extensions;

    // ImageMagick here has no OpenMP: each image uses one core (libheif adds a few threads of its own for HEIC tiles).
    public int MaxParallel => 8;

    // The header is read off the caller's thread, like every image operation reads its file.
    public Task<string> RunAsync(string inputPath, CancellationToken ct) => Task.Run(async () =>
    {
        using var share = await (budget ?? MemoryBudget.Shared).AcquireAsync(MemoryBudget.EstimateNeed(inputPath), ct).ConfigureAwait(false);
        return await inner.RunAsync(inputPath, ct).ConfigureAwait(false);
    }, ct);
}
