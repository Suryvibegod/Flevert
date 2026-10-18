namespace ConvertAnything.Core;

public interface IOperation
{
    /// <summary>Stable id used in registry commands, e.g. <c>image.to-png</c>. Never rename once released.</summary>
    string Id { get; }

    /// <summary>Menu location below the ConvertAnything entry; <c>/</c> separates submenus, e.g. <c>Convert/PNG</c>.</summary>
    string MenuPath { get; }

    /// <summary>Lower-case file extensions with leading dot this operation accepts.</summary>
    IReadOnlyCollection<string> Extensions { get; }

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

public sealed class OperationCatalog(IEnumerable<IOperation> operations)
{
    public IReadOnlyList<IOperation> All { get; } = [.. operations];

    public IOperation? Find(string id) => All.FirstOrDefault(op => op.Id == id);
}
