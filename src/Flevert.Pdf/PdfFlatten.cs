using System.Text.Json.Nodes;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Flatten forms and comments</c> (docs/specs/pdf.md, v0.2.2): writes <c>&lt;name&gt; (flattened).pdf</c>, in which
/// filled form fields and comments (notes, stamps, highlights) are part of the page: they print and show the same
/// everywhere, and nobody can change the answers in a viewer. Text stays text and drawings stay vector, never a picture
/// of the page as Stirling-PDF makes it. A PDF without form fields or comments is skipped.
/// </summary>
/// <remarks>
/// <para>
/// qpdf flattens (<c>--flatten-annotations=all</c>): it draws each visible annotation's appearance into the page content
/// and removes it, and removes the form (<c>/AcroForm</c>) once every field is drawn, so no field is left to fill
/// (success criterion 5). A form that asks viewers to draw its fields (<c>/NeedAppearances</c>) gets them drawn first
/// (<c>--generate-appearances</c>). pdfium's <c>FPDFPage_Flatten</c>, which the spec named, leaves the form's fields in
/// the catalog, and draws nothing for a field without an appearance; qpdf runs out of process with the limits of every
/// qpdf operation, so no pdfium worker is needed (deviation noted in tasks/pdf/todo.md).
/// </para>
/// <para>
/// Whether there is anything to flatten is read from qpdf's description of the PDF without stream data (as Remove
/// metadata reads it): a field (<c>/FT</c>), or an annotation with an appearance (<c>/AP</c> and <c>/Rect</c>). The
/// description goes in a private folder under the temporary folder, deleted when the operation ends.
/// </para>
/// </remarks>
public sealed class PdfFlatten : IOperation
{
    internal const string NothingMessage = "This PDF has no forms or comments to flatten.";

    private readonly Qpdf _qpdf;

    public PdfFlatten(Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
    }

    public string Id => "pdf.flatten";

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // qpdf processes, mostly reading and writing (PdfRotate.MaxParallel).
    public int MaxParallel => 4;

    /// <summary>Where the operation's private folders go: the user's temporary folder, or a test's.</summary>
    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    public async Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var pages = await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);
        if (!await HasSomethingToFlattenAsync(inputPath, ct).ConfigureAwait(false))
            throw new OperationSkippedException(NothingMessage);

        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (flattened).pdf");
        var output = await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
        {
            var repaired = await _qpdf.TransformAsync(inputPath, temp,
                [Qpdf.KeepStreamsAsTheyAre, "--generate-appearances", "--flatten-annotations=all"], token).ConfigureAwait(false);
            await PdfRepair.CheckAsync(_qpdf, repaired, pages, temp, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (signed)
            OutputNote.Add(PdfSignature.NotValidNote);
        return output;
    }

    private async Task<bool> HasSomethingToFlattenAsync(string inputPath, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetFullPath(ScratchRoot), $"flevert-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);
        try
        {
            var described = Path.Combine(scratch, "pdf.json");
            await _qpdf.TransformAsync(inputPath, described, ["--json-output=2", "--json-stream-data=none"], ct).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => HasSomethingToFlatten(described, ct), ct).ConfigureAwait(false);
            }
            catch (InvalidDataException e)
            {
                throw new PdfException(PdfError.NotPdf, PdfDocument.NotPdfMessage, e);
            }
        }
        finally
        {
            try
            {
                // Only qpdf's description is in it: nobody else can add to it, so nothing in it is a link.
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder, readable only by this user, SYSTEM and administrators.
            }
        }
    }

    /// <summary>Whether qpdf's description at <paramref name="describedPath"/> holds a form field or an annotation with an appearance.</summary>
    internal static bool HasSomethingToFlatten(string describedPath, CancellationToken ct)
    {
        var found = false;
        QpdfJsonFile.Read(describedPath, (element, key, value) =>
        {
            if (found || element != QpdfJsonFile.Objects || !key.StartsWith("obj:", StringComparison.Ordinal) || value is not JsonObject entry)
                return;
            var dictionary = entry["value"] as JsonObject ?? (entry["stream"] as JsonObject)?["dict"] as JsonObject;
            if (dictionary is not null && (dictionary.ContainsKey("/FT") || (dictionary.ContainsKey("/AP") && dictionary.ContainsKey("/Rect"))))
                found = true;
        }, ct);
        return found;
    }
}
