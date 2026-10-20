using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// What an operation that writes a PDF does when qpdf had to repair the PDF it read (exit code 3; docs/specs/pdf.md, P4,
/// in place of a Repair item): the output is kept, with a note to check it, unless it has other than the pages counted
/// in the input, which fails the file. qpdf rebuilds a damaged cross-reference table by scanning the file for objects,
/// and what it finds may not be every page the PDF had.
/// </summary>
internal static class PdfRepair
{
    public const string RepairedNote = "Flevert had to repair this PDF while reading it: check that every page is there.";

    public const string MissingPagesMessage = "This PDF is damaged: the result would miss pages.";

    /// <summary>
    /// Called on the output before it is committed: when qpdf <paramref name="repaired"/> the input, counts the pages of
    /// <paramref name="outputPath"/>, and fails unless they are the <paramref name="pages"/> the input was counted with.
    /// </summary>
    public static async Task CheckAsync(Qpdf qpdf, bool repaired, int pages, string outputPath, CancellationToken ct)
    {
        if (!repaired)
            return;
        if (await qpdf.CountPagesAsync(outputPath, ct).ConfigureAwait(false) != pages)
            throw new PdfException(PdfError.MissingPages, MissingPagesMessage);
        OutputNote.Add(RepairedNote);
    }

    /// <summary>The note alone, for an output whose pages cannot be counted again (an encrypted one) or are counted already.</summary>
    public static void Note(bool repaired)
    {
        if (repaired)
            OutputNote.Add(RepairedNote);
    }
}
