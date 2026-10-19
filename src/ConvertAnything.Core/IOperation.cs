namespace ConvertAnything.Core;

public interface IOperation
{
    /// <summary>Stable id used in registry commands, e.g. <c>image.to-png</c>. Never rename once released.</summary>
    string Id { get; }

    /// <summary>Menu location below the ConvertAnything entry; <c>/</c> separates submenus, e.g. <c>Convert/PNG</c>.</summary>
    string MenuPath { get; }

    /// <summary>Lower-case file extensions with leading dot this operation accepts.</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>
    /// How many files of a batch this operation may convert at once (ca.exe also caps it by the cores it has). One unless
    /// the operation says otherwise: work that already uses every core, such as ffmpeg, gains nothing from more.
    /// </summary>
    int MaxParallel => 1;

    /// <summary>Processes one file and returns the path of the file it produced.</summary>
    /// <remarks>
    /// Contract for implementations:
    /// <list type="bullet">
    /// <item>Write only through <see cref="OutputPath.WriteAtomicallyAsync"/>; never write next to the input directly.</item>
    /// <item>Open the input read-only with at least <see cref="FileShare.Read"/>; the runner holds it pinned meanwhile.</item>
    /// <item>Honour <paramref name="ct"/> promptly. An operation that ignores it is abandoned, not awaited.</item>
    /// <item>Do not return or throw while a child process or handle still uses the temporary output, or a
    /// <c>.partial</c> file may be left behind.</item>
    /// </list>
    /// </remarks>
    Task<string> RunAsync(string inputPath, CancellationToken ct);
}

/// <summary>
/// Thrown by an operation when the input already satisfies the request, so there is nothing worth writing (for example
/// "already well compressed"). Reported as skipped, with the message and no stack trace, and not as a failure; a request
/// the operation cannot fulfil is a failure, not a skip. Throw it directly (not wrapped), before any output is committed.
/// </summary>
public sealed class OperationSkippedException : Exception
{
    public OperationSkippedException()
        : base("Nothing to gain.")
    {
    }

    public OperationSkippedException(string message)
        : base(message)
    {
    }

    public OperationSkippedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class OperationCatalog(IEnumerable<IOperation> operations)
{
    public IReadOnlyList<IOperation> All { get; } = [.. operations];

    public IOperation? Find(string id) => All.FirstOrDefault(op => op.Id == id);
}
