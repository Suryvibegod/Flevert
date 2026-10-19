using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image;

public enum CompressLevel
{
    Light,
    Balanced,
    Strong,
}

/// <summary>
/// <c>Compress/&lt;Level&gt;</c>: a smaller copy in the same format where that format compresses well, otherwise JPG.
/// Metadata is removed except the colour profile, which changes how the image looks.
/// </summary>
public sealed class ImageCompress(CompressLevel level) : IOperation
{
    private const uint StrongMaxEdge = 2560;

    // Spelled out: ids are stored in the registry and must never change if the enum or its names do.
    public string Id => level switch
    {
        CompressLevel.Light => "image.compress-light",
        CompressLevel.Balanced => "image.compress-balanced",
        _ => "image.compress-strong",
    };

    public string MenuPath => "Compress/" + level;

    public IReadOnlyCollection<string> Extensions { get; } = [.. ImageFile.ReadableExtensions];

    private uint Quality => level switch
    {
        CompressLevel.Light => 85,
        CompressLevel.Balanced => 75,
        _ => 60,
    };

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var sourceExtension = Path.GetExtension(inputPath);
        // The image is read before the output is named: whether a GIF stays a GIF depends on it being animated.
        return Task.Run(async () =>
        {
            var source = ImageFile.FormatOf(sourceExtension);
            using var images = ImageFile.Read(inputPath, allFrames: ImageFile.IsAnimationFormat(source));
            ImageFile.StopOnCancel(images, ct);
            var target = Target(source, animated: images.Count > 1);
            // Keep the user's spelling (.jpeg, .JPG) when the format stays the same.
            var extension = target == ImageFile.FormatOf(sourceExtension) ? sourceExtension : ".jpg";
            var desired = Path.Combine(
                Path.GetDirectoryName(inputPath)!, $"{Path.GetFileNameWithoutExtension(inputPath)} (compressed){extension}");

            return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => ImageFile.RunAsync(() =>
            {
                if (ImageFile.IsAnimationFormat(target) && images.Count > 1)
                {
                    ImageFile.Coalesce(images);
                    foreach (var frame in images)
                        Prepare(frame, target);
                    if (target == MagickFormat.Gif)
                        images.Optimize();
                    ImageFile.Write(images, temp, target);
                }
                else
                {
                    var image = images[0];
                    Prepare(image, target);
                    ImageFile.Write(image, temp, target);
                }

                // An already well-compressed source (a chat download at quality 70, a tiny PNG) can grow; a bigger
                // "compressed" copy would mislead, so refuse it and let the temporary file be deleted.
                if (new FileInfo(temp).Length >= new FileInfo(inputPath).Length)
                    throw new InvalidOperationException("The image is already well compressed: the result is not smaller than the original.");
            }, token), ct).ConfigureAwait(false);
        }, ct);
    }

    private static MagickFormat Target(MagickFormat source, bool animated) => source switch
    {
        MagickFormat.Jpeg or MagickFormat.Png or MagickFormat.WebP or MagickFormat.Avif => source,
        MagickFormat.Gif when animated => MagickFormat.Gif,
        _ => MagickFormat.Jpeg, // HEIC/HEIF cannot be written here; GIF, BMP and TIFF compress far better as JPG
    };

    private void Prepare(IMagickImage<byte> image, MagickFormat target)
    {
        image.AutoOrient();
        ImageMetadata.StripExceptColourProfile(image);
        ImageMetadata.ToSrgb(image); // also drops the large CMYK profile from the output

        if (target == MagickFormat.Png)
        {
            if (level == CompressLevel.Strong)
                image.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            image.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
            return;
        }

        if (target == MagickFormat.Gif)
            return; // palette format; frames were coalesced and are optimised as a whole

        if (target == MagickFormat.Jpeg)
            ImageMetadata.FlattenOntoWhite(image);

        if (level == CompressLevel.Strong && Math.Max(image.Width, image.Height) > StrongMaxEdge)
            image.Resize(new MagickGeometry(StrongMaxEdge, StrongMaxEdge)); // keeps the aspect ratio, only shrinks here

        image.Quality = Quality;
    }
}
