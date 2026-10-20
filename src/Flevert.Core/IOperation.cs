namespace Flevert.Core;

/// <summary>
/// What the menu, the catalog and <c>list-ops</c> need to know about an operation of either kind: one file in, one output
/// (<see cref="IOperation"/>), or every file of the batch in, one output (<see cref="IBatchOperation"/>).
/// </summary>
public interface IMenuOperation
{
    /// <summary>Stable id used in registry commands, e.g. <c>image.to-png</c>. Never rename once released.</summary>
    string Id { get; }

    /// <summary>
    /// Menu location below the Flevert entry; <c>/</c> separates submenus, e.g. <c>Convert/PNG</c>. Empty for an
    /// operation that is not in the menu and runs only by its id (the PDF tools of v0.2.2, which their window runs).
    /// </summary>
    string MenuPath { get; }

    /// <summary>Lower-case file extensions with leading dot this operation accepts.</summary>
    IReadOnlyCollection<string> Extensions { get; }
}

/// <summary>Converts each file of a batch on its own: one output per input.</summary>
public interface IOperation : IMenuOperation
{
    /// <summary>
    /// How many files of a batch this operation may convert at once (flevert.exe also caps it by the cores it has). One unless
    /// the operation says otherwise: work that already uses every core, such as ffmpeg, gains nothing from more.
    /// </summary>
    int MaxParallel => 1;

    /// <summary>Processes one file and returns the path of the file, or folder, it produced.</summary>
    /// <remarks>
    /// Contract for implementations:
    /// <list type="bullet">
    /// <item>Write only through <see cref="OutputPath.WriteAtomicallyAsync"/>, or <see cref="OutputPath.WriteDirectoryAtomicallyAsync"/>
    /// for a folder of outputs (return the folder); never write next to the input directly.</item>
    /// <item>Open the input read-only with at least <see cref="FileShare.Read"/>; the runner holds it pinned meanwhile.</item>
    /// <item>Honour <paramref name="ct"/> promptly. An operation that ignores it is abandoned, not awaited.</item>
    /// <item>Do not return or throw while a child process or handle still uses the temporary output, or a
    /// <c>.partial</c> file or folder may be left behind.</item>
    /// </list>
    /// </remarks>
    Task<string> RunAsync(string inputPath, CancellationToken ct);

    /// <summary>
    /// What the runner calls. Same contract as <see cref="RunAsync(string, CancellationToken)"/>, and the operation may
    /// report how far it is inside the file ("page 37 of 200"), which the progress window shows under the batch line.
    /// </summary>
    /// <remarks>
    /// The default ignores <paramref name="progress"/>, so an operation without such progress implements only the other
    /// overload. One that has it implements this one and makes the other call it with <c>null</c> (never the reverse as
    /// well, which would recurse). <paramref name="progress"/> may be <c>null</c>; report from any thread, in order, no
    /// more often than real work is done; reports after the file is finished are ignored. ADR-0007.
    /// </remarks>
    Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct) => RunAsync(inputPath, ct);
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

/// <summary>
/// Joins every file of a batch into one output, such as merging PDFs (ADR-0007, part 2). The runner waits until the batch
/// is complete, pins every file for the whole run and calls the operation once.
/// </summary>
public interface IBatchOperation : IMenuOperation
{
    /// <summary>Joins <paramref name="inputPaths"/> into one file, or folder, and returns its path.</summary>
    /// <param name="inputPaths">At least one: the real paths of the pinned inputs, distinct, in Explorer's natural order of
    /// their names (<see cref="NaturalOrder"/>). The operation decides whether one file is enough; if not, it throws
    /// with a message the user can act on ("Select at least two PDFs").</param>
    /// <param name="progress">May be <c>null</c>; report how far the operation is, such as "file 3 of 12".</param>
    /// <remarks>
    /// The contract of <see cref="IOperation.RunAsync(string, CancellationToken)"/> applies, and also:
    /// <list type="bullet">
    /// <item>Write the output beside the first input (the folder of <c>inputPaths[0]</c>).</item>
    /// <item>Nothing is written unless every input could be used: when one cannot, throw <see cref="BatchInputException"/>
    /// naming it, so the notification can say which file stopped the batch.</item>
    /// </list>
    /// </remarks>
    Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Thrown by an <see cref="IBatchOperation"/> when one input cannot be used (a corrupt image, a damaged PDF), so the whole
/// output fails and the notification names that file. Create it with <see cref="ForInput"/>; the message says what is
/// wrong with the file, without its name.
/// </summary>
/// <remarks>
/// The standard constructors exist for the usual exception contract and name no input, so the failure is reported with
/// its message alone, like any other failure. A path and a message are both strings, which is why the constructor that
/// takes both is private: <c>new BatchInputException(path, message)</c> and <c>(message, …)</c> were easy to swap.
/// </remarks>
public sealed class BatchInputException : Exception
{
    public BatchInputException()
    {
    }

    public BatchInputException(string message)
        : base(message)
    {
    }

    public BatchInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private BatchInputException(string inputPath, string message, Exception? innerException)
        : base(message, innerException)
    {
        InputPath = inputPath;
    }

    /// <param name="inputPath">The input that cannot be used, exactly as the operation received it.</param>
    /// <param name="message">What is wrong with it, without its name ("This photo is damaged.").</param>
    public static BatchInputException ForInput(string inputPath, string message, Exception? innerException = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        return new BatchInputException(inputPath, message, innerException);
    }

    /// <summary>The input that could not be used, as the operation received it; <c>null</c> when none was named.</summary>
    public string? InputPath { get; }
}

public sealed class OperationCatalog
{
    public OperationCatalog(IEnumerable<IMenuOperation> operations)
    {
        All = [.. operations];
        // flevert.exe runs an operation by its kind: it must be exactly one of them.
        foreach (var op in All)
        {
            if (op is IOperation == op is IBatchOperation)
                throw new ArgumentException($"'{op.Id}' must be either an {nameof(IOperation)} or an {nameof(IBatchOperation)}.", nameof(operations));
        }
    }

    public IReadOnlyList<IMenuOperation> All { get; }

    public IMenuOperation? Find(string id) => All.FirstOrDefault(op => op.Id == id);
}
