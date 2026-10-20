using System.Globalization;

namespace Flevert.Core.Operations;

/// <summary>
/// Joins the text files of a batch into one, in the order it receives them (natural order), with progress per file, to
/// exercise many-to-one operations end to end; installed with <c>install-menu --dev</c> like <see cref="DevCopy"/>.
/// Output: <c>&lt;first name&gt; (+N).txt</c> beside the first file, N being how many more files were joined; a single file
/// gives <c>&lt;name&gt; (joined).txt</c>, since "(+0)" reads like a mistake.
/// </summary>
public sealed class DevJoin : IBatchOperation
{
    public string Id => "dev.join";

    public string MenuPath => "Dev/Join";

    public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

    public Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputPaths.Count, 1);
        var first = inputPaths[0];
        var more = inputPaths.Count == 1 ? "joined" : "+" + (inputPaths.Count - 1).ToString(CultureInfo.InvariantCulture);
        var desired = Path.Combine(Path.GetDirectoryName(first)!, $"{Path.GetFileNameWithoutExtension(first)} ({more}).txt");

        return OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
        {
            await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            for (var i = 0; i < inputPaths.Count; i++)
            {
                progress?.Report(new FileProgress("file", i + 1, inputPaths.Count));
                await using var source = new FileStream(inputPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await source.CopyToAsync(target, token).ConfigureAwait(false);
            }
        }, ct);
    }
}
