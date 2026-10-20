using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Merge PDFs</c> (docs/specs/pdf.md): every page of every selected PDF, in the order the runner gives them (Explorer's
/// natural order of the names), into <c>&lt;first name&gt; (merged).pdf</c> beside the first. qpdf copies the pages as
/// they are, so nothing on them is re-encoded. All or nothing (ADR-0007): a PDF that cannot be used fails the merge,
/// naming it, and nothing is written.
/// </summary>
/// <remarks>
/// One qpdf process reads every input (<see cref="Qpdf.MergeAsync"/>), so its memory cap and timeout go by their total
/// size. There is no progress within the merge: qpdf reports none, and it is one step.
/// </remarks>
public sealed class PdfMerge : IBatchOperation
{
    internal const string NeedsTwoMessage = "Select at least two PDFs to merge.";

    internal static readonly string TooManyPagesTogetherMessage = string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"Together these PDFs have more than {PdfDocument.MaxPages} pages; Flevert makes PDFs of at most {PdfDocument.MaxPages}.");

    private readonly Qpdf _qpdf;

    public PdfMerge(Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
    }

    /// <summary>Run after the pages of each input are counted, before it is checked for a signature, for tests: an input
    /// changing meanwhile, or one that can no longer be read.</summary>
    internal Func<string, Task>? AfterCounting { get; init; }

    public string Id => "pdf.merge";

    public string MenuPath => "Merge PDFs";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public async Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        // Names no file: the selection is what is wrong, not the one PDF in it.
        if (inputPaths.Count < 2)
            throw new BatchInputException(NeedsTwoMessage);

        // The merged PDF is a PDF like any other, so at most PdfDocument.MaxPages in all (T18). Each input is counted on its
        // own, so one that cannot be used is named.
        var total = 0;
        var signed = false;
        foreach (var input in inputPaths)
        {
            try
            {
                total += await _qpdf.CountPagesAsync(input, ct).ConfigureAwait(false);
                if (AfterCounting is not null)
                    await AfterCounting(input).ConfigureAwait(false);
                signed |= await PdfSignature.IsSignedAsync(input, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is PdfException or IOException or UnauthorizedAccessException)
            {
                throw BatchInputException.ForInput(input, e.Message, e);
            }
        }

        if (total > PdfDocument.MaxPages)
        {
            throw new PdfException(PdfError.TooManyPages, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Together these PDFs have {total} pages; Flevert makes PDFs of at most {PdfDocument.MaxPages}."));
        }

        var first = inputPaths[0];
        var desired = Path.Combine(Path.GetDirectoryName(first)!, Path.GetFileNameWithoutExtension(first) + " (merged).pdf");
        var output = await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
        {
            await _qpdf.MergeAsync(inputPaths, temp, token).ConfigureAwait(false);
            // An input read again for the merge may not be the one counted (a file changing on a share): the limit holds
            // for what was written, which fails here rather than being kept, in the words of a merge.
            try
            {
                await _qpdf.CountPagesAsync(temp, token).ConfigureAwait(false);
            }
            catch (PdfException e) when (e.Error == PdfError.TooManyPages)
            {
                throw new PdfException(PdfError.TooManyPages, TooManyPagesTogetherMessage, e);
            }
        }, ct).ConfigureAwait(false);
        if (signed)
            OutputNote.Add(PdfSignature.NotValidNote);
        return output;
    }
}
