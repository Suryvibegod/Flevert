using System.Text;
using Flevert.Core;
using Flevert.Ocr;
using ImageMagick;

namespace Flevert.Image;

/// <summary>
/// <c>Convert to/Text</c> (docs/specs/ocr.md, v1.4.0): writes <c>&lt;name&gt;.txt</c> beside the picture with the recognised
/// text in UTF-8 with a byte order mark and CRLF line endings.
/// </summary>
/// <remarks>
/// Decodes the picture with the existing safe Magick path (auto-orient by EXIF, first frame of GIF/multi-page TIFF).
/// Preprocessed to an 8-bit grayscale PNG in a private temp folder, scaled to what tesseract reads well (<see cref="OcrSize"/>:
/// small text doubled, very large pictures shrunk), and with resolution density set so Tesseract does not warn. The picture
/// is turned upright first when OSD finds it turned (<see cref="ReadUprightAsync{T}"/>), before ReadTextAsync recognises the text.
/// If no text is recognised, the operation is skipped with "No text was found in this picture."
/// </remarks>
public sealed class ImageToText : IOperation
{
    private readonly Tesseract _tesseract;

    public ImageToText()
        : this(Tesseract.NextToApp)
    {
    }

    internal ImageToText(Tesseract tesseract)
    {
        ArgumentNullException.ThrowIfNull(tesseract);
        _tesseract = tesseract;
    }

    public string Id => "image.to-text";

    public string MenuPath => "Convert to/Text";

    public IReadOnlyCollection<string> Extensions => ImageFile.ReadableExtensions;

    /// <summary>Where the operation's private temporary folders go: the system temp folder or a test's.</summary>
    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + ".txt");
        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => ProcessAsync(inputPath, temp, token), ct);
    }

    private async Task ProcessAsync(string inputPath, string tempOutputPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Only this user may change the picture tesseract reads: %TEMP% may let other accounts swap it.
        var scratch = Path.Combine(Path.GetFullPath(ScratchRoot), $"flevert-ocr-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);
        var tempPng = Path.Combine(scratch, "input.png");

        try
        {
            await ImageFile.RunAsync(() =>
            {
                using var images = ImageFile.Read(inputPath, allFrames: false);
                ImageFile.StopOnCancel(images, ct);
                var image = images[0];

                ImageMetadata.AutoOrient(image);
                ImageMetadata.FlattenOntoWhite(image);

                var (width, height) = OcrSize(image.Width, image.Height);
                if ((width, height) != (image.Width, image.Height))
                    image.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });

                image.ColorSpace = ColorSpace.Gray;
                image.ColorType = ColorType.Grayscale;
                image.Depth = 8;
                image.Density = new Density(300, 300, DensityUnit.PixelsPerInch);

                ImageFile.Write(image, tempPng, MagickFormat.Png);
            }, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var clean = CleanText(await ReadUprightAsync(_tesseract, tempPng, ct).ConfigureAwait(false));

            if (clean.Trim().Length == 0)
                throw new OperationSkippedException("No text was found in this picture.");

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            await File.WriteAllTextAsync(tempOutputPath, clean + "\r\n", utf8, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder left in %TEMP% must not hide the result or the real error.
            }
        }
    }

    /// <summary>
    /// The OSD confidence under which its answer is not acted on: 1.5. Pictures without text scored 0.01-0.85 with a turn
    /// at random; a line of text 1.7-2.7 and two lines 2.8-4.7, each time with the right turn (docs/specs/ocr.md).
    /// </summary>
    internal const double UnsureOsdFloor = 1.5;

    /// <summary>
    /// How far apart the mean word confidences of two readings may be and still count as alike: 2 points. A page read
    /// upright and the same page turned a quarter clockwise, which tesseract reads as well, differ by up to 0.03; a page
    /// read upright and read upside down or turned a quarter back by 22 or more (docs/specs/ocr.md).
    /// </summary>
    internal const double AlikeConfidence = 2;

    /// <summary>
    /// The text of <paramref name="pngPath"/>, a grey PNG Flevert wrote at 300 DPI in a folder of its own, read upright
    /// (<see cref="ReadUprightAsync{T}"/>): scanned pages of a PDF too.
    /// </summary>
    internal static async Task<string> ReadUprightAsync(Tesseract tesseract, string pngPath, CancellationToken ct) =>
        (await ReadTextUprightAsync(tesseract, pngPath, ct).ConfigureAwait(false)).Reading;

    /// <inheritdoc cref="ReadUprightAsync(Tesseract, string, CancellationToken)"/>
    internal static Task<Upright<string>> ReadTextUprightAsync(Tesseract tesseract, string pngPath, CancellationToken ct) =>
        ReadUprightAsync(
            tesseract,
            pngPath,
            tesseract.ReadTextAsync,
            async (picture, token) =>
            {
                // Beside the picture, in Flevert's folder: <name>-text.txt and .tsv.
                var reading = await tesseract.ReadAsync(picture, Path.ChangeExtension(picture, null) + "-text", token).ConfigureAwait(false);
                return (reading.Text, reading.Quality);
            },
            ct);

    /// <summary>
    /// Reads <paramref name="pngPath"/>, a grey PNG Flevert wrote at 300 DPI in a folder of its own, upright, and returns
    /// the reading with how many degrees clockwise the picture was turned for it (0, 90, 180 or 270); the picture is left
    /// turned as it was read.
    /// </summary>
    /// <param name="read">Reads a picture once.</param>
    /// <param name="readRated">Reads a picture once, with how sure tesseract was of the reading.</param>
    /// <remarks>
    /// <para>
    /// OSD sure of a turn (<see cref="Tesseract.ConfidentOsd"/>) turns the picture before it is read, as before. OSD finds
    /// the right turn of pages in sans-serif fonts too, but less sure of it: five lines of Arial score 8-9, thirty 16-19, so
    /// such a page was read upside down. Unsure (from <see cref="UnsureOsdFloor"/>), the picture is read both as it is
    /// and turned, at once, and the better reading is kept (<see cref="TurnedReadsBetter"/>): one more reading, on those
    /// pages only. An upright page stays as it is.
    /// </para>
    /// <para>
    /// OSD that fails (crashes, runs out of memory or time) leaves the page as it is: the orientation is a refinement,
    /// and reading the text may still succeed. The PNG is read back as PNG only, under the module's policy and limits:
    /// on the PDF paths the pdf worker wrote it, and nothing else in flevert.exe may have set ImageMagick up yet.
    /// </para>
    /// </remarks>
    internal static async Task<Upright<T>> ReadUprightAsync<T>(
        Tesseract tesseract,
        string pngPath,
        Func<string, CancellationToken, Task<T>> read,
        Func<string, CancellationToken, Task<(T Reading, ReadingQuality Quality)>> readRated,
        CancellationToken ct)
    {
        OsdAnswer osd;
        try
        {
            osd = await tesseract.DetectOrientationAsync(pngPath, ct).ConfigureAwait(false);
        }
        catch (OcrException)
        {
            ct.ThrowIfCancellationRequested();
            osd = default;
        }

        if (osd.Rotate == 0 || osd.Confidence < UnsureOsdFloor)
            return new Upright<T>(0, await read(pngPath, ct).ConfigureAwait(false));

        if (osd.Confidence >= Tesseract.ConfidentOsd)
        {
            await TurnAsync(pngPath, pngPath, osd.Rotate, ct).ConfigureAwait(false);
            return new Upright<T>(osd.Rotate, await read(pngPath, ct).ConfigureAwait(false));
        }

        var turnedPath = Path.ChangeExtension(pngPath, null) + "-turned.png";
        try
        {
            await TurnAsync(pngPath, turnedPath, osd.Rotate, ct).ConfigureAwait(false);
            // Both at once: through the gate, as any two readings.
            var asIs = readRated(pngPath, ct);
            var turned = readRated(turnedPath, ct);
            await Task.WhenAll(asIs, turned).ConfigureAwait(false);
            var (asIsReading, asIsQuality) = await asIs.ConfigureAwait(false);
            var (turnedReading, turnedQuality) = await turned.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (!TurnedReadsBetter(asIsQuality, turnedQuality))
                return new Upright<T>(0, asIsReading);

            File.Move(turnedPath, pngPath, overwrite: true);
            return new Upright<T>(osd.Rotate, turnedReading);
        }
        finally
        {
            try
            {
                File.Delete(turnedPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Flevert's folder goes with it.
            }
        }
    }

    /// <summary>
    /// Whether the picture read turned reads better than as it is: the higher mean word confidence, or, when the two are
    /// alike (<see cref="AlikeConfidence"/>), more words on lines running across the picture, as an upright page's do.
    /// Still alike, the page stays as it is.
    /// </summary>
    internal static bool TurnedReadsBetter(ReadingQuality asIs, ReadingQuality turned)
    {
        if (turned.Words == 0)
            return false;
        if (asIs.Words == 0)
            return true;

        var gain = turned.MeanConfidence - asIs.MeanConfidence;
        if (Math.Abs(gain) > AlikeConfidence)
            return gain > 0;
        return turned.WordsAcross > asIs.WordsAcross;
    }

    // Writes the PNG at source turned clockwise to destination, which may be the same file.
    private static async Task TurnAsync(string source, string destination, int degrees, CancellationToken ct)
    {
        await ImageFile.RunAsync(() =>
        {
            MagickSetup.EnsureInitialized();
            byte[] png;
            // Opened here and handed over as a stream with the format pinned: ImageMagick neither guesses the
            // format from the content nor reads patterns in the path.
            using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var rotated = new MagickImage(stream, new MagickReadSettings { Format = MagickFormat.Png }))
            {
                ImageFile.StopOnCancel([rotated], ct);
                rotated.Rotate(degrees);
                rotated.Density = new Density(300, 300, DensityUnit.PixelsPerInch);
                png = rotated.ToByteArray(MagickFormat.Png);
            }

            File.WriteAllBytes(destination, png);
        }, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>A reading of a picture, and how many degrees clockwise the picture was turned for it.</summary>
    internal readonly record struct Upright<T>(int Turned, T Reading);

    /// <summary>
    /// The most pixels handed to tesseract: about a 6000 x 4200 px page. Tesseract's memory and time grow with the area,
    /// and three A4 pages at 300 DPI read well within its 1 GB cap and 150 s.
    /// </summary>
    internal const ulong MaxOcrPixels = 25_000_000;

    /// <summary>The longest side handed to tesseract, below the 32767 px it accepts and ImageMagick's 40000 px.</summary>
    internal const uint MaxOcrSide = 30_000;

    /// <summary>
    /// The size a picture is read at: doubled when its shorter side is under 1000 px (screenshot text is too small for
    /// tesseract), but only as far as <see cref="MaxOcrPixels"/> and <see cref="MaxOcrSide"/> allow, and shrunk to them
    /// when it is larger (50-200 MP photos). The area, not the long side, is capped, so a long screenshot keeps its width
    /// and stays readable.
    /// </summary>
    internal static (uint Width, uint Height) OcrSize(uint width, uint height)
    {
        var scale = Math.Min(width, height) < 1000 ? 2.0 : 1.0;
        scale = Math.Min(scale, Math.Sqrt(MaxOcrPixels / ((double)width * height)));
        scale = Math.Min(scale, (double)MaxOcrSide / Math.Max(width, height));
        if (scale == 1.0)
            return (width, height);

        // The tiny allowance keeps a side that the side cap sets exactly (30000) from flooring to 29999.
        return (Scaled(width), Scaled(height));
        uint Scaled(uint side) => Math.Max(1u, (uint)Math.Floor(side * scale + 1e-6));
    }

    internal static string CleanText(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (IsBlankSpace(c))
                clean.Append(' ');
            else if (c is not ('\0' or '\f' or '￾' or '￿'))
                clean.Append(c);
        }

        return clean.ToString().ReplaceLineEndings("\r\n").TrimEnd();
    }

    private static bool IsBlankSpace(char c) => c is ' ' or (>= ' ' and <= ' ') or ' ' or ' ' or '　';
}
