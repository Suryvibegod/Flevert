using Flevert.Core;
using ImageMagick;

namespace Flevert.Image;

/// <summary><c>Convert to/&lt;FORMAT&gt;</c>: writes the image in another format beside the original.</summary>
/// <param name="quality">Encoder quality for lossy targets, or <c>null</c> for the encoder's default.</param>
public sealed class ImageConvert(string extension, MagickFormat format, uint? quality = null) : IOperation
{
    public string Id => $"image.to-{extension.TrimStart('.')}";

    public string MenuPath => "Convert to/" + (extension == ".webp" ? "WebP" : extension.TrimStart('.').ToUpperInvariant());

    // Not to its own format, and not a HEIC or HEIF photo to GIF: 256 colours ruin a photo, and that entry would be the
    // 17th of their menu, past the 16 Explorer shows (docs/specs/image.md).
    public IReadOnlyCollection<string> Extensions { get; } =
        [.. ImageFile.ReadableExtensions.Where(e => ImageFile.FormatOf(e) != format
            && !(format == MagickFormat.Gif && ImageFile.FormatOf(e) is MagickFormat.Heic or MagickFormat.Heif))];

    private bool HasAlpha => format is not (MagickFormat.Jpeg or MagickFormat.Bmp);

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + extension);
        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => ImageFile.RunAsync(() =>
        {
            // Only an animation stays one; the pages of a TIFF are not frames, and other targets hold one image. Read here,
            // off the caller's thread: a slow share or an offline cloud file must not freeze the progress window.
            var source = ImageFile.RealFormat(inputPath);
            var keepsAnimation = ImageFile.IsAnimationFormat(format) && ImageFile.IsAnimationFormat(source);
            var lossyPhoto = ImageFile.IsLossyPhotoSource(inputPath, source);
            using var images = ImageFile.Read(inputPath, allFrames: keepsAnimation);
            ImageFile.StopOnCancel(images, token);
            if (images.Count > 1)
            {
                // Frames of an optimised GIF only hold what changed; expand them so each one stands on its own.
                ImageFile.Coalesce(images, token);
                foreach (var frame in images)
                    Prepare(frame, lossyPhoto);
                ImageFile.Write(images, temp, format);
                return;
            }

            var image = images[0];
            Prepare(image, lossyPhoto);
            ImageFile.Write(image, temp, format);
        }, token), ct);
    }

    private void Prepare(IMagickImage<byte> image, bool lossyPhotoSource)
    {
        // Rotate the pixels so viewers that ignore EXIF orientation still show the image upright.
        ImageMetadata.AutoOrient(image);
        ImageMetadata.ToSrgb(image);
        if (!HasAlpha)
            ImageMetadata.FlattenOntoWhite(image);

        if (quality is { } q)
            image.Quality = q;

        ImageMetadata.PhotoSubsamplingForJpeg(image, lossyPhotoSource, format);

        // The TIFF writer defaults to no compression: an 8 KB PNG of 8000x8000 became an 8 MB TIFF (measured; far more
        // for photos).
        if (format == MagickFormat.Tiff)
            image.Settings.Compression = CompressionMethod.Zip;
    }
}
