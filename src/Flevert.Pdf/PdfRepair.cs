using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// What an operation that writes a PDF does when qpdf had to repair the PDF it read (exit code 3; docs/specs/pdf.md, P4,
/// in place of a Repair item): the output is kept, with a note to check it, unless it does not open or has other than
/// the pages qpdf counted in the input, which fails the file.
/// </summary>
/// <remarks>
/// The count of the input is qpdf's too, made by the same repair: pages the repair itself loses (a damaged
/// cross-reference table rebuilt by scanning the file for objects may not find them all) cannot be told from pages the
/// PDF never had, since every reader repairs the same way. The note asks the user to look.
/// </remarks>
internal static class PdfRepair
{
    public const string RepairedNote = "Flevert had to repair this PDF while reading it: check that the pages look right.";

    public const string MissingPagesMessage = "This PDF is damaged: the result would be missing pages.";

    public const string OtherPagesMessage = "This PDF is damaged: the result would not have the same pages.";

    public const string UnreadableMessage = "This PDF is too damaged: Flevert could not write a copy of it that opens.";

    /// <summary>
    /// Called on the output before it is committed: when qpdf <paramref name="repaired"/> the input, counts the pages of
    /// <paramref name="outputPath"/>, and fails unless they are the <paramref name="pages"/> the input was counted with.
    /// </summary>
    /// <remarks>
    /// The output may not open at all: qpdf 12.4.2, given a PDF with neither cross-reference table nor trailer, finds its
    /// pages by their catalog but writes no /Root and no /Size in the output's trailer. Written plainly, qpdf finds the
    /// catalog again when it reads the output; packed into object streams (Compress), it cannot (probed).
    /// </remarks>
    public static async Task CheckAsync(Qpdf qpdf, bool repaired, int pages, string outputPath, CancellationToken ct)
    {
        if (!repaired)
            return;
        var written = await CountWrittenAsync(qpdf, outputPath, ct).ConfigureAwait(false)
            ?? throw new PdfException(PdfError.RepairFailed, UnreadableMessage);

        RequireCounted(written, pages);
        OutputNote.Add(RepairedNote);
    }

    /// <summary>Fails unless an output written from a repaired input has the <paramref name="counted"/> pages.</summary>
    public static void RequireCounted(int written, int counted)
    {
        if (written != counted)
            throw new PdfException(PdfError.RepairFailed, written < counted ? MissingPagesMessage : OtherPagesMessage);
    }

    /// <summary>The pages qpdf counts in the output it wrote, or <c>null</c> when it cannot read that output.</summary>
    public static async Task<int?> CountWrittenAsync(Qpdf qpdf, string outputPath, CancellationToken ct)
    {
        try
        {
            return await qpdf.CountPagesAsync(outputPath, ct).ConfigureAwait(false);
        }
        catch (PdfException e) when (e.Error == PdfError.NotPdf)
        {
            return null;
        }
    }

    /// <summary>The note alone, for an output whose pages cannot be counted again (an encrypted one) or are counted already.</summary>
    public static void Note(bool repaired)
    {
        if (repaired)
            OutputNote.Add(RepairedNote);
    }
}
