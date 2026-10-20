using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Compress</c> (docs/specs/pdf.md, lossless): writes <c>&lt;name&gt; (compressed).pdf</c> beside the original, with the
/// same content stored in less space. Nothing is re-encoded with loss: images and pages are what they were, so a scan
/// made of JPEGs gets little smaller (that is Compress for sharing, if it comes). A result that is not smaller than the
/// original is no use, so the file is skipped and nothing is written.
/// </summary>
/// <remarks>
/// qpdf (12.4.2) gathers the objects that are not streams into compressed object streams (<c>--object-streams=generate</c>,
/// PDF 1.5, which qpdf raises the version to), compresses every stream left uncompressed (<c>--compress-streams=y</c>,
/// also the default) and decodes every Flate stream to compress it again at the best level
/// (<c>--recompress-flate --compression-level=9</c>; without <c>--recompress-flate</c> qpdf copies a Flate stream as it
/// is). Streams in the old LZW, ASCII85 or run-length filters are written as Flate (qpdf's default decode level). A
/// digital signature no longer matches the rewritten file.
/// </remarks>
public sealed class PdfCompress : IOperation
{
    internal const string AlreadyCompactMessage = "This PDF is already compact: compressing it would not make it smaller.";

    private static readonly string[] Options =
        ["--object-streams=generate", "--compress-streams=y", "--recompress-flate", "--compression-level=9"];

    private readonly Qpdf _qpdf;

    public PdfCompress(Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
    }

    public string Id => "pdf.compress";

    public string MenuPath => "Compress";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // Like the rotations: one single-threaded qpdf per file, mostly reading and writing (PdfRotate.MaxParallel).
    public int MaxParallel => 4;

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (compressed).pdf");
        return OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
        {
            await _qpdf.TransformAsync(inputPath, temp, Options, token).ConfigureAwait(false);
            // Thrown before the output is committed: WriteAtomicallyAsync deletes the temporary file.
            if (new FileInfo(temp).Length >= new FileInfo(inputPath).Length)
                throw new OperationSkippedException(AlreadyCompactMessage);
        }, ct);
    }
}
