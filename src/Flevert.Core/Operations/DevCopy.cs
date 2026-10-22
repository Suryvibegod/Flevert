namespace Flevert.Core.Operations;

/// <summary>Copies a file. Exists only to exercise the menu and batching end to end; installed with <c>install-menu --dev</c>.</summary>
public sealed class DevCopy : IOperation
{
    public string Id => "dev.copy";

    public string MenuPath => "Dev/Copy";

    public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

    // Mostly waiting on the disk; parallel like the image operations, so batching is exercised the same way.
    public int MaxParallel => 8;

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var desired = Path.Combine(
            Path.GetDirectoryName(inputPath)!,
            $"{Path.GetFileNameWithoutExtension(inputPath)} (copy){Path.GetExtension(inputPath)}");

        return OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
        {
            await using var source = new FileStream(PinnedInput.ReadPath(inputPath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(target, token).ConfigureAwait(false);
        }, ct);
    }
}
