using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image;

/// <summary><c>Convert/&lt;FORMAT&gt;</c>: writes the image in another format beside the original.</summary>
/// <param name="quality">Encoder quality for lossy targets, or <c>null</c> for the encoder's default.</param>
public sealed class ImageConvert(string extension, MagickFormat format, uint? quality = null) : IOperation
{
    public string Id => $"image.to-{extension.TrimStart('.')}";

    public string MenuPath => $"Convert/{extension.TrimStart('.').ToUpperInvariant()}";

    public IReadOnlyCollection<string> Extensions { get; } =
        [.. ImageFile.ReadableExtensions.Where(e => ImageFile.FormatOf(e) != format)];

    private bool HasAlpha => format is not (MagickFormat.Jpeg or MagickFormat.Bmp);

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + extension);
        // Only an animation stays one; the pages of a TIFF are not frames, and other targets hold one image.
        var keepsAnimation = ImageFile.IsAnimationFormat(format) && ImageFile.IsAnimationFormat(ImageFile.FormatOf(Path.GetExtension(inputPath)));
        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => ImageFile.RunAsync(() =>
        {
            using var images = ImageFile.Read(inputPath, allFrames: keepsAnimation);
            ImageFile.StopOnCancel(images, token);
            if (images.Count > 1)
            {
                // Frames of an optimised GIF only hold what changed; expand them so each one stands on its own.
                ImageFile.Coalesce(images);
                foreach (var frame in images)
                    Prepare(frame);
                ImageFile.Write(images, temp, format);
                return;
            }

            var image = images[0];
            Prepare(image);
            ImageFile.Write(image, temp, format);
        }, token), ct);
    }

    private void Prepare(IMagickImage<byte> image)
    {
        // Rotate the pixels so viewers that ignore EXIF orientation still show the image upright.
        image.AutoOrient();
        ImageMetadata.ToSrgb(image);
        if (!HasAlpha)
            ImageMetadata.FlattenOntoWhite(image);

        if (quality is { } q)
            image.Quality = q;
    }
}
