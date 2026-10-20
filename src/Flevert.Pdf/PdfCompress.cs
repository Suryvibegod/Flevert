using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Compress/Lossless</c> (docs/specs/pdf.md; a menu item of its own until 0.2.0, "Compress"): writes <c>&lt;name&gt; (compressed).pdf</c> beside the original, with the
/// same content stored in less space. Nothing is re-encoded with loss: images and pages are what they were, so a scan
/// made of JPEGs gets little smaller (that is <see cref="PdfCompressSharing"/>). A result that is not smaller than the
/// original is no use, so the file is skipped and nothing is written, unless qpdf had to repair the PDF: then the
/// repaired copy is written whatever its size, with the note that says so.
/// </summary>
/// <remarks>
/// <para>
/// qpdf (12.4.2) gathers the objects that are not streams into compressed object streams (<c>--object-streams=generate</c>,
/// PDF 1.5, which qpdf raises the version to), compresses every stream left uncompressed (<c>--compress-streams=y</c>,
/// also the default) and decodes every Flate stream to compress it again at the best level
/// (<c>--recompress-flate --compression-level=9</c>; without <c>--recompress-flate</c> qpdf copies a Flate stream as it
/// is). Streams in the old LZW, ASCII85 or run-length filters are written as Flate (qpdf's default decode level). A
/// digital signature no longer matches the rewritten file.
/// </para>
/// <para>
/// Decoding is the one thing here whose cost does not follow the file's size: 10 KB of Flate inside Flate inflates to
/// 4 GB, which took 15 to 20 s (T18), and a PDF can hold many. So the full run gets 3 s plus 1 s per megabyte of input
/// (<see cref="FullRunTimeoutFor"/>), ample for real PDFs (qpdf rewrites tens of megabytes a second, and real streams
/// inflate a few times over, rarely a hundred), and past it, or past qpdf's memory cap, the PDF is compressed again
/// without decoding anything: object streams only, every stream copied as it is (<see cref="Qpdf.KeepStreamsAsTheyAre"/>),
/// which takes the time of reading and writing the file. The result says it was compressed only in part.
/// </para>
/// <para>
/// A PDF qpdf had to repair is checked to open with the pages counted in the input (<see cref="PdfRepair"/>). qpdf 12.4.2
/// writes no /Root for a PDF it found without cross-reference table or trailer, and cannot read such a file back when its
/// objects are in object streams; that one is written again without object streams, its streams still compressed.
/// </para>
/// <para>
/// Like every operation that writes a PDF, at most <see cref="PdfDocument.MaxPages"/> pages (counted first, walking the
/// page tree), and a signed PDF gets a note that its signature is not valid in the output.
/// </para>
/// </remarks>
public sealed class PdfCompress : IOperation
{
    internal const string AlreadyCompactMessage = "This PDF is already compact: compressing it would not make it smaller.";

    /// <summary>The note on a PDF compressed without decoding its streams.</summary>
    internal const string PartlyNote = "Flevert compressed this PDF only in part: compressing it fully took too long.";

    private const string ObjectStreams = "--object-streams=generate";
    private const string NoObjectStreams = "--object-streams=disable";

    private static readonly string[] Options = [ObjectStreams, "--compress-streams=y", "--recompress-flate", "--compression-level=9"];

    private static readonly string[] WithoutDecoding = [Qpdf.KeepStreamsAsTheyAre, ObjectStreams];

    private readonly Qpdf _qpdf;

    public PdfCompress(Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
    }

    public string Id => "pdf.compress";

    public string MenuPath => "Compress/Lossless";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // Like the rotations: one single-threaded qpdf per file, mostly reading and writing (PdfRotate.MaxParallel).
    public int MaxParallel => 4;

    /// <summary>How long the run that decodes and compresses every stream again may take for an input of <paramref name="inputBytes"/>.</summary>
    public static TimeSpan FullRunTimeoutFor(long inputBytes) => TimeSpan.FromSeconds(3 + inputBytes / (1024 * 1024));

    public async Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var pages = await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);

        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (compressed).pdf");
        var partly = false;
        var output = await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
        {
            var length = new FileInfo(inputPath).Length;
            bool repaired;
            try
            {
                repaired = await _qpdf.TransformAsync(inputPath, temp, Options, token, FullRunTimeoutFor(length)).ConfigureAwait(false);
            }
            catch (PdfException e) when (e.Error is PdfError.TooSlow or PdfError.NeedsTooMuchMemory)
            {
                // qpdf has exited; it writes its output anew, over what the stopped run left.
                repaired = await _qpdf.TransformAsync(inputPath, temp, WithoutDecoding, token).ConfigureAwait(false);
                partly = true;
            }

            // qpdf 12.4.2 writes no /Root for some PDFs it repaired, and then cannot find their catalog again inside object
            // streams (PdfRepair): the same run without them, streams still compressed, gives a copy that opens.
            if (repaired && await PdfRepair.CountWrittenAsync(_qpdf, temp, token).ConfigureAwait(false) is null)
            {
                var options = partly ? WithoutDecoding : Options;
                await _qpdf.TransformAsync(inputPath, temp, [.. options.Select(o => o == ObjectStreams ? NoObjectStreams : o)], token,
                    partly ? null : FullRunTimeoutFor(length)).ConfigureAwait(false);
            }

            await PdfRepair.CheckAsync(_qpdf, repaired, pages, temp, token).ConfigureAwait(false);

            // Thrown before the output is committed: WriteAtomicallyAsync deletes the temporary file. A PDF qpdf had to repair
            // is written whatever its size: the repaired copy is worth having (the README says Lossless repairs PDFs).
            if (!repaired && new FileInfo(temp).Length >= length)
                throw new OperationSkippedException(AlreadyCompactMessage);
        }, ct).ConfigureAwait(false);
        if (partly)
            OutputNote.Add(PartlyNote);
        if (signed)
            OutputNote.Add(PdfSignature.NotValidNote);
        return output;
    }
}
