using Flevert.Core;
using ImageMagick;

namespace Flevert.Image;

/// <summary>
/// <c>Remove hidden info</c>: a copy without EXIF (camera, GPS location), XMP, IPTC or comments, for sharing. The ICC
/// profile stays because it changes how the colours look; the pixels are rotated first because the orientation tag is
/// removed with the EXIF. The format is kept, except HEIC/HEIF, which become JPG.
/// </summary>
public sealed class ImageStripMetadata : IOperation
{
    public string Id => "image.strip-metadata";

    public string MenuPath => "Remove hidden info";

    public IReadOnlyCollection<string> Extensions { get; } = [.. ImageFile.ReadableExtensions];

    // The format is read off the caller's thread: a slow share or an offline cloud file must not freeze the progress window.
    public Task<string> RunAsync(string inputPath, CancellationToken ct) => Task.Run(() =>
    {
        var source = ImageFile.RealFormat(inputPath);
        var target = source is MagickFormat.Heic or MagickFormat.Heif ? MagickFormat.Jpeg : source; // HEIC cannot be written
        var desired = Path.Combine(
            Path.GetDirectoryName(inputPath)!,
            $"{Path.GetFileNameWithoutExtension(inputPath)} (no metadata){ImageFile.ExtensionFor(target, inputPath)}");

        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => ImageFile.RunAsync(() =>
        {
            var lossless = target == MagickFormat.WebP && ImageFile.IsLosslessWebP(inputPath);
            // Animations and TIFF pages are all kept; formats with one image need only that one decoded.
            var keepsFrames = ImageFile.IsAnimationFormat(target) || target == MagickFormat.Tiff;
            using var images = ImageFile.Read(inputPath, allFrames: keepsFrames);
            // TIFF thumbnails can still show the image as it was before editing; they are metadata, not pages.
            foreach (var thumbnail in images.Where(ImageFile.IsThumbnail).ToList())
            {
                if (images.Count == 1)
                    break;
                images.Remove(thumbnail);
                thumbnail.Dispose();
            }

            ImageFile.StopOnCancel(images, token);
            // One compression is written for every page. Keep the source's when all pages share it; a scanner that mixes
            // Group 4 text pages with colour pages gets lossless Zip, or its colour pages would turn black and white.
            var tiffCompressions = images.Select(f => f.Compression).Distinct().ToList();
            var tiffCompression = tiffCompressions.Count == 1 ? tiffCompressions[0] : CompressionMethod.Zip;
            // gif2webp-style frames cover the canvas and are alpha-blended (transparent = keep the previous frame);
            // ImageMagick writes WebP frames without blending, so expand them first or most of each frame turns clear.
            if (target == MagickFormat.WebP && images.Count > 1)
                ImageFile.Coalesce(images, token);
            foreach (var frame in images)
            {
                ImageMetadata.AutoOrient(frame);
                ImageMetadata.StripExceptColourProfile(frame);
                // The pixels are re-encoded; ImageMagick only estimates the source quality of a JPEG, so WebP, AVIF and a
                // HEIC becoming JPG would otherwise fall back to its default (visibly worse than Compress > Light).
                if (lossless)
                    frame.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
                else if (target is MagickFormat.WebP or MagickFormat.Avif || (target == MagickFormat.Jpeg && source != MagickFormat.Jpeg))
                    frame.Quality = HighQuality;
                if (target == MagickFormat.Jpeg)
                    ImageMetadata.FlattenOntoWhite(frame); // HEIC/HEIF with transparency becoming JPG
                ImageMetadata.PhotoSubsamplingForJpeg(frame, source is MagickFormat.Heic or MagickFormat.Heif, target);
                // The TIFF writer ignores the compression the file was read with and writes uncompressed: 2 KB became 2 MB.
                if (target == MagickFormat.Tiff)
                {
                    frame.Settings.Compression = tiffCompression;
                    // The TIFF reader reports quality 0, so JPEG-in-TIFF would be re-encoded at libtiff's default (~75).
                    if (tiffCompression == CompressionMethod.JPEG)
                        frame.Quality = HighQuality;
                }
            }

            if (images.Count > 1)
                ImageFile.Write(images, temp, target);
            else
                ImageFile.Write(images[0], temp, target);
        }, token), ct);
    }, ct);

    private const uint HighQuality = 95;
}
