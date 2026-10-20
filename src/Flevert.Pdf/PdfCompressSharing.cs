using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Compress/Smallest, for sharing</c> (docs/specs/pdf.md, P1): writes <c>&lt;name&gt; (small).pdf</c> beside the original, every
/// image drawn above 150 DPI wherever it is drawn brought down to 150 DPI as a JPEG of quality 80: a 25 MB scan becomes a
/// file that goes through Zalo or email, and still reads on a phone and prints on A4. Images at 150 DPI or less, 1-bit
/// images (fax, JBIG2, stencil masks), text and drawings stay as they are, byte for byte. A result not at least 5%
/// smaller is skipped, and nothing is written.
/// </summary>
/// <remarks>
/// <para>
/// The PDF worker (pdfium) reads which images each page draws and at what resolution, and decodes, shrinks and encodes
/// those to change, so no image data of an untrusted PDF is decoded in flevert.exe (<see cref="PdfWorker.ShrinkImagesAsync"/>).
/// qpdf then describes the PDF with each stream's stored data in a file (<see cref="Qpdf.DescribeWithStreamDataAsync"/>),
/// <see cref="ImageReplacement"/> finds the images again by the SHA-256 of that data, and <c>--update-from-json</c> writes
/// the PDF with the JPEGs in their place, every other stream copied as it is.
/// </para>
/// <para>
/// The JPEGs, the description and the stream data go in a folder of their own under the temporary folder that only this
/// user, SYSTEM and administrators can change (<see cref="PrivateFolder"/>), and are deleted when the operation ends.
/// The stream data is about the PDF's size: the drive must have twice that free before anything is written, and a file
/// that cannot be written there fails as a disk problem, never as a damaged PDF.
/// Like every operation that writes a PDF: at most <see cref="PdfDocument.MaxPages"/> pages, a note for a signed PDF and
/// for one qpdf had to repair.
/// </para>
/// </remarks>
public sealed class PdfCompressSharing : IOperation
{
    /// <summary>The resolution images are brought down to, and the one above which they are.</summary>
    public const double TargetDpi = 150;

    internal const string AlreadySmallMessage = "This PDF is already small: compressing it for sharing would not make it much smaller.";

    /// <summary>The result must be at most this much of the original, or it is not worth a second file.</summary>
    private const double MaxShare = 0.95;

    private readonly Qpdf _qpdf;
    private readonly PdfWorker _worker;

    public PdfCompressSharing(Qpdf qpdf)
        : this(qpdf, PdfWorker.NextToApp)
    {
    }

    /// <param name="worker">The flevert.exe that reads the PDF; tests give the one they built.</param>
    internal PdfCompressSharing(Qpdf qpdf, PdfWorker worker)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
        _worker = worker;
    }

    public string Id => "pdf.compress-sharing";

    public string MenuPath => "Compress/Smallest, for sharing";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // One worker at a time, as for pictures (PdfToImages.MaxParallel): it decodes images of up to 100 MP.
    public int MaxParallel => 1;

    /// <summary>Where the operation's private folders go: the user's temporary folder, or a test's.</summary>
    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    /// <summary>The free bytes of the volume a folder is on; tests give a smaller disk.</summary>
    internal Func<string, long?> FreeSpace { get; init; } = PdfRenderBudget.FreeBytes;

    /// <summary>Run with the private folder before qpdf describes the PDF into it, for tests: a file qpdf cannot write.</summary>
    internal Action<string>? BeforeDescribing { get; init; }

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var pages = await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);

        var root = Path.GetFullPath(ScratchRoot);
        var drive = Path.GetPathRoot(root);
        // qpdf writes every stream of the PDF, as stored, into the folder, and the worker its JPEGs: about the PDF's size,
        // and room to spare. Checked before anything is written; a disk that fills meanwhile fails as a disk below.
        if (FreeSpace(root) is { } free && free < 2 * new FileInfo(inputPath).Length)
            throw new IOException($"There is not enough free space on {drive} to compress this PDF for sharing.");

        var scratch = Path.Combine(root, $"flevert-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);
        string output;
        try
        {
            var shrunk = await _worker.ShrinkImagesAsync(inputPath, scratch, TargetDpi, progress, ct).ConfigureAwait(false);
            if (shrunk.Count == 0)
                throw new OperationSkippedException(AlreadySmallMessage);

            var described = Path.Combine(scratch, "pdf.json");
            var prefix = Path.Combine(scratch, "s");
            BeforeDescribing?.Invoke(scratch);
            var update = Path.Combine(scratch, "update.json");
            int replaced;
            try
            {
                await _qpdf.DescribeWithStreamDataAsync(inputPath, described, prefix, ct).ConfigureAwait(false);
                replaced = await Task.Run(() => ImageReplacement.WriteUpdate(described, prefix, shrunk, update, ct), ct).ConfigureAwait(false);
            }
            catch (InvalidDataException e)
            {
                throw new PdfException(PdfError.NotPdf, PdfDocument.NotPdfMessage, e);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Flevert could not write its working files on {drive} to compress this PDF for sharing: check that the disk has free space.", e);
            }

            if (replaced == 0)
                throw new OperationSkippedException(AlreadySmallMessage);

            var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (small).pdf");
            output = await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
            {
                var repaired = await _qpdf.UpdateFromJsonAsync(inputPath, update, temp, token).ConfigureAwait(false);
                await PdfRepair.CheckAsync(_qpdf, repaired, pages, temp, token).ConfigureAwait(false);
                // Thrown before the output is committed: WriteAtomicallyAsync deletes the temporary file.
                if (new FileInfo(temp).Length > new FileInfo(inputPath).Length * MaxShare)
                    throw new OperationSkippedException(AlreadySmallMessage);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // Only the worker's JPEGs and qpdf's files are in it: nobody else can add to it, so nothing in it is a link.
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder, readable only by this user, SYSTEM and administrators.
            }
        }

        if (signed)
            OutputNote.Add(PdfSignature.NotValidNote);
        return output;
    }
}
