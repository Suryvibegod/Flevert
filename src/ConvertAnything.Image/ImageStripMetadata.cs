using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image;

/// <summary>
/// <c>Remove metadata</c>: a copy without EXIF (camera, GPS location), XMP, IPTC or comments, for sharing. The ICC
/// profile stays because it changes how the colours look; the pixels are rotated first because the orientation tag is
/// removed with the EXIF. The format is kept, except HEIC/HEIF, which become JPG.
/// </summary>
public sealed class ImageStripMetadata : IOperation
{
    public string Id => "image.strip-metadata";

    public string MenuPath => "Remove metadata";

    public IReadOnlyCollection<string> Extensions { get; } = [.. ImageFile.ReadableExtensions];

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var sourceExtension = Path.GetExtension(inputPath);
        var source = ImageFile.FormatOf(sourceExtension);
        var target = source is MagickFormat.Heic or MagickFormat.Heif ? MagickFormat.Jpeg : source;
        var extension = target == source ? sourceExtension : ".jpg";
        var desired = Path.Combine(
            Path.GetDirectoryName(inputPath)!, $"{Path.GetFileNameWithoutExtension(inputPath)} (no metadata){extension}");

        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => Task.Run(() =>
        {
            var lossless = target == MagickFormat.WebP && ImageFile.IsLosslessWebP(inputPath);
            // Animations and TIFF pages are all kept; formats with one image need only that one decoded.
            var keepsFrames = ImageFile.IsAnimationFormat(target) || target == MagickFormat.Tiff;
            using var images = ImageFile.Read(inputPath, allFrames: keepsFrames);
            foreach (var frame in images)
            {
                frame.AutoOrient();
                ImageMetadata.StripExceptColourProfile(frame);
                // The pixels are re-encoded; for WebP and AVIF ImageMagick has no estimate of the source quality and would
                // fall back to its default (visibly worse than Compress > Light), so use a high one. JPEG keeps its own
                // estimate of the source quality.
                if (lossless)
                    frame.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
                else if (target is MagickFormat.WebP or MagickFormat.Avif)
                    frame.Quality = HighQuality;
            }

            if (images.Count > 1)
                ImageFile.Write(images, temp, target);
            else
                ImageFile.Write(images[0], temp, target);
        }, token), ct);
    }

    private const uint HighQuality = 95;
}
