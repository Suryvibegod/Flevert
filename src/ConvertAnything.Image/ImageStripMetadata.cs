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
            using var images = ImageFile.Read(inputPath);
            foreach (var frame in images)
            {
                frame.AutoOrient();
                ImageMetadata.StripExceptColourProfile(frame);
            }

            if (images.Count > 1 && target is MagickFormat.Gif or MagickFormat.WebP)
                images.Write(temp, target);
            else
                images[0].Write(temp, target);
        }, token), ct);
    }
}
