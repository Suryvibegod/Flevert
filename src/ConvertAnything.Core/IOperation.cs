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
    Task<string> RunAsync(string inputPath, CancellationToken ct);
}

public sealed class OperationCatalog(IEnumerable<IOperation> operations)
{
    public IReadOnlyList<IOperation> All { get; } = [.. operations];

    public IOperation? Find(string id) => All.FirstOrDefault(op => op.Id == id);
}
