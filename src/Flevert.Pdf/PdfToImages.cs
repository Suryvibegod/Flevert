using System.Globalization;
using ImageMagick;
using Flevert.Core;
using Flevert.Image;

namespace Flevert.Pdf;

/// <summary>The image format <see cref="PdfToImages"/> writes each page in.</summary>
public enum PdfPageImageFormat
{
    Jpeg,
    Png,
}

/// <summary>
/// <c>Convert/JPG pages</c> and <c>Convert/PNG pages</c> (docs/specs/pdf.md): renders every page with pdfium at
/// <see cref="Dpi"/> (lower for a page past <see cref="PdfDocument.MaxRenderPixels"/>) into a folder
/// <c>&lt;name&gt; (pages)</c> of <c>&lt;name&gt; 001.jpg</c>…, written whole or not at all, with progress per page.
/// </summary>
/// <remarks>
/// A page pdfium cannot read fails the whole file, naming the page: the folder is atomic, and one with a page silently
/// missing would look complete. ImageMagick only encodes pixels rendered here; it never parses the PDF.
/// </remarks>
public sealed class PdfToImages : IOperation
{
    /// <summary>Readable on a phone and small (docs/specs/pdf.md, "Render").</summary>
    public const double Dpi = 150;

    private const uint JpegQuality = 90;
    private const int MaxFileNameLength = 255;

    // What a page holds in memory at once: the rendered BGRA pixels, ImageMagick's copy of them (Q8, four channels: the
    // same size) and the encoded file, which for a page is at most about the size of the raw pixels.
    private const ulong Copies = 3;

    private readonly PdfPageImageFormat _format;
    private readonly double _dpi;
    private readonly MemoryBudget _budget;

    public PdfToImages(PdfPageImageFormat format)
        : this(format, Dpi, MemoryBudget.Shared)
    {
    }

    /// <param name="dpi">A lower DPI lets tests render many pages quickly; the output is named the same way.</param>
    /// <param name="budget">Shared with the image operations, so pages and images converted at once share one limit.</param>
    internal PdfToImages(PdfPageImageFormat format, double dpi, MemoryBudget budget)
    {
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format), format, null);
        _format = format;
        _dpi = dpi;
        _budget = budget;
    }

    public string Id => _format == PdfPageImageFormat.Jpeg ? "pdf.to-jpg" : "pdf.to-png";

    public string MenuPath => _format == PdfPageImageFormat.Jpeg ? "Convert/JPG pages" : "Convert/PNG pages";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // pdfium renders one page at a time behind its process-wide lock (ADR-0008): a second file at once would only wait.
    public int MaxParallel => 1;

    private string ImageExtension => _format == PdfPageImageFormat.Jpeg ? ".jpg" : ".png";

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    // Off the caller's thread: opening parses the PDF, and rendering and encoding are synchronous.
    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        using var pdf = PdfDocument.Open(inputPath);
        var total = pdf.PageCount;
        if (total == 0)
            throw new PdfException("This PDF has no pages.");

        MagickSetup.EnsureInitialized();
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, $"{stem} (pages)");
        var digits = Math.Max(3, total.ToString(CultureInfo.InvariantCulture).Length);

        return await OutputPath.WriteDirectoryAtomicallyAsync(desired, async (folder, token) =>
        {
            for (var index = 0; index < total; index++)
            {
                token.ThrowIfCancellationRequested();
                var page = index + 1;
                progress?.Report(new FileProgress("page", page, total));

                // Reserved before the pixels exist; a share is granted at once when it fits, without looking at the token.
                var size = pdf.GetRenderSize(index, _dpi);
                using var share = await _budget.AcquireAsync(checked((ulong)size.Bytes * Copies), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var encoded = Encode(pdf.Render(index, _dpi), token);
                var name = PageName(stem, page.ToString("D" + digits, CultureInfo.InvariantCulture));
                var stream = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using (stream.ConfigureAwait(false))
                    await stream.WriteAsync(encoded, token).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }, ct);

    private byte[] Encode(PdfBitmap bitmap, CancellationToken ct)
    {
        using var image = new MagickImage();
        // Raw pixels in memory: no coder, so no file format is parsed here.
        image.ReadPixels(bitmap.Bgra.Span, new PixelReadSettings((uint)bitmap.Width, (uint)bitmap.Height, StorageType.Char, PixelMapping.BGRA));
        // pdfium drew the page on white, so every pixel is opaque: RGB output, no alpha channel.
        image.Alpha(AlphaAction.Off);
        // Prints at the page's real size.
        image.Density = new Density(bitmap.Dpi, bitmap.Dpi, DensityUnit.PixelsPerInch);
        if (_format == PdfPageImageFormat.Jpeg)
        {
            image.Quality = JpegQuality;
            // ImageMagick keeps full colour resolution from quality 90 up; pages are for reading and sharing, and 4:2:0
            // is the smaller file (spec: "nhỏ").
            image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
        }

        // A 10000 px page takes a while to encode; stop it when the user cancels.
        ImageFile.StopOnCancel([image], ct);
        try
        {
            return image.ToByteArray(_format == PdfPageImageFormat.Jpeg ? MagickFormat.Jpeg : MagickFormat.Png);
        }
        catch (MagickException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    // "<name> 007.jpg", with the name shortened so a long input name still gives a valid page name.
    private string PageName(string stem, string number)
    {
        var suffix = $" {number}{ImageExtension}";
        var room = MaxFileNameLength - suffix.Length;
        if (stem.Length > room)
        {
            stem = stem[..room];
            if (char.IsHighSurrogate(stem[^1]))
                stem = stem[..^1];
        }

        return stem + suffix;
    }
}

/// <summary>
/// The pdfium render operations (<c>pdf.to-jpg</c>, <c>pdf.to-png</c>). A list of its own beside the qpdf operations, so
/// the catalog adds both.
/// </summary>
public static class PdfRenderOperations
{
    public static IReadOnlyList<IOperation> All { get; } =
        [new PdfToImages(PdfPageImageFormat.Jpeg), new PdfToImages(PdfPageImageFormat.Png)];
}
