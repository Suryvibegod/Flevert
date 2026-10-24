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
/// Preprocessed to an 8-bit grayscale PNG in a private temp folder, upscaled 2x if its shorter side is under 1000 px,
/// and with resolution density set so Tesseract does not warn. When confident, DetectRotationAsync rotates the image upright,
/// before ReadTextAsync recognises the text.
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

                if (Math.Min(image.Width, image.Height) < 1000)
                    image.Resize(new Percentage(200));

                image.ColorSpace = ColorSpace.Gray;
                image.ColorType = ColorType.Grayscale;
                image.Depth = 8;
                image.Density = new Density(300, 300, DensityUnit.PixelsPerInch);

                image.Format = MagickFormat.Png;
                image.Write(tempPng);
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
    /// The text of <paramref name="pngPath"/>, a grey PNG Flevert wrote at 300 DPI in a folder of its own: turned upright
    /// first, in place, when OSD is confident it is turned (scanned pages of a PDF too).
    /// </summary>
    internal static async Task<string> ReadUprightAsync(Tesseract tesseract, string pngPath, CancellationToken ct)
    {
        await TurnUprightAsync(tesseract, pngPath, ct).ConfigureAwait(false);
        return await tesseract.ReadTextAsync(pngPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns <paramref name="pngPath"/>, a grey PNG Flevert wrote at 300 DPI, upright in place when OSD is confident it
    /// is turned, and returns how many degrees clockwise it turned it (0, 90, 180 or 270).
    /// </summary>
    internal static async Task<int> TurnUprightAsync(Tesseract tesseract, string pngPath, CancellationToken ct)
    {
        var rotation = await tesseract.DetectRotationAsync(pngPath, ct).ConfigureAwait(false);
        if (rotation != 0)
        {
            await ImageFile.RunAsync(() =>
            {
                using var rotated = new MagickImage(pngPath);
                ImageFile.StopOnCancel([rotated], ct);
                rotated.Rotate(rotation);
                rotated.Density = new Density(300, 300, DensityUnit.PixelsPerInch);
                rotated.Format = MagickFormat.Png;
                rotated.Write(pngPath);
            }, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return rotation;
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
