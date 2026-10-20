using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Remove metadata</c> (docs/specs/pdf.md): writes <c>&lt;name&gt; (no metadata).pdf</c> beside the original, without
/// the document information (but <c>/Producer (Flevert)</c>), XMP, private application data, attachments and
/// JavaScript (<see cref="MetadataRemoval"/> lists them). The pages are copied as they are, so they look the same; a
/// digital signature no longer matches the changed file.
/// </summary>
/// <remarks>
/// qpdf has no option for most of this (<c>--remove-info</c> keeps no producer, <c>--remove-metadata</c> only takes the
/// document's XMP, and neither touches scripts), so it runs twice: <c>--json-output</c> describes every object of the
/// PDF without its stream data, <see cref="MetadataRemoval"/> turns that into an update of the objects that change, and
/// <c>--update-from-json</c> writes the PDF with them. The description and the update repeat the metadata, so they go in
/// a folder of their own under the temporary folder that only this user, SYSTEM and administrators can change
/// (<see cref="PrivateFolder"/>), never beside the input, and are deleted when the operation ends.
/// </remarks>
public sealed class PdfStripMetadata : IOperation
{
    private readonly Qpdf _qpdf;

    public PdfStripMetadata(Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
    }

    public string Id => "pdf.strip-metadata";

    public string MenuPath => "Remove metadata";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // Like the rotations: one single-threaded qpdf at a time per file, mostly reading and writing (PdfRotate.MaxParallel).
    public int MaxParallel => 4;

    /// <summary>Where the operation's private folders go: the user's temporary folder, or a test's.</summary>
    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    public async Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        // At most 2 000 pages, like every PDF operation (T18), and a note when a signature no longer matches.
        await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);

        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (no metadata).pdf");
        var output = await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => WriteAsync(inputPath, temp, token), ct).ConfigureAwait(false);
        if (signed)
            OutputNote.Add(PdfSignature.NotValidNote);
        return output;
    }

    private async Task WriteAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetFullPath(ScratchRoot), $"flevert-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);
        try
        {
            var described = Path.Combine(scratch, "pdf.json");
            await _qpdf.TransformAsync(inputPath, described, ["--json-output=2", "--json-stream-data=none"], ct).ConfigureAwait(false);
            var update = Path.Combine(scratch, "update.json");
            try
            {
                await Task.Run(() => MetadataRemoval.WriteUpdate(described, update, ct), ct).ConfigureAwait(false);
            }
            catch (InvalidDataException e)
            {
                throw new PdfException(PdfError.NotPdf, PdfDocument.NotPdfMessage, e);
            }

            await _qpdf.UpdateFromJsonAsync(inputPath, update, outputPath, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // Only qpdf's files and the update are in it: nobody else can add to it, so nothing in it is a link.
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder, readable only by this user, SYSTEM and administrators.
            }
        }
    }
}
