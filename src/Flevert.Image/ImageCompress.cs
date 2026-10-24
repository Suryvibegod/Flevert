using Flevert.Core;
using ImageMagick;

namespace Flevert.Image;

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
    private const double MinSavedShare = 0.95;

    // Spelled out: ids are stored in the registry and must never change if the enum or its names do.
    public string Id => level switch
    {
        CompressLevel.Light => "image.compress-light",
        CompressLevel.Balanced => "image.compress-balanced",
        _ => "image.compress-strong",
    };

    // Says what the level does for the user (docs/specs/pdf.md, v0.2.2); the id keeps the old name.
    public string MenuPath => "Compress/" + level switch
    {
        CompressLevel.Light => "Light (best quality)",
        CompressLevel.Balanced => "Balanced",
        _ => "Strong (smallest)",
    };

    public IReadOnlyCollection<string> Extensions { get; } = [.. ImageFile.ReadableExtensions];

    private uint Quality => level switch
    {
        CompressLevel.Light => 85,
        CompressLevel.Balanced => 75,
        _ => 60,
    };

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        // The image is read before the output is named: whether a GIF stays a GIF depends on it being animated.
        return Task.Run(async () =>
        {
            var source = ImageFile.RealFormat(inputPath);
            // The result is one JPG: for a TIFF, its one real page (thumbnails skipped); a second page fails, since
            // Compress cannot fulfil the request without dropping it (a skip would claim there is nothing to gain).
            using var images = source == MagickFormat.Tiff
                ? ImageFile.ReadFirstPage(inputPath)
                : ImageFile.Read(inputPath, allFrames: ImageFile.IsAnimationFormat(source));
            ImageFile.StopOnCancel(images, ct);
            var target = Target(source, animated: images.Count > 1);
            var desired = Path.Combine(
                Path.GetDirectoryName(inputPath)!,
                $"{Path.GetFileNameWithoutExtension(inputPath)} (compressed){ImageFile.ExtensionFor(target, inputPath)}");

            return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => ImageFile.RunAsync(() =>
            {
                if (ImageFile.IsAnimationFormat(target) && images.Count > 1)
                {
                    ImageFile.Coalesce(images, token);
                    foreach (var frame in images)
                        Prepare(frame, target);
                    if (target == MagickFormat.Gif)
                    {
                        images.Optimize();
                        ImageFile.StopOnCancel(images, token); // Optimize replaces the frames too
                    }
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
                // Less than 5% saved is not worth a second, re-encoded copy (a q80 JPEG at Light saves a fraction of a percent).
                if (new FileInfo(temp).Length > PinnedInput.Length(inputPath) * MinSavedShare)
                {
                    // An animated GIF is only re-optimised, never made lossier, so "already well compressed" would be false
                    // for a GIF that is simply too large to send. Say what would help.
                    if (target == MagickFormat.Gif && images.Count > 1)
                        throw new InvalidOperationException("Compress cannot make this animated GIF meaningfully smaller; use Convert to > WebP for a much smaller animation.");
                    throw new OperationSkippedException("The image is already well compressed: the result would be less than 5% smaller.");
                }
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
        ImageMetadata.AutoOrient(image);
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
        {
            ImageMetadata.FlattenOntoWhite(image);
            // Light keeps the source's subsampling; the stronger levels trade colour resolution for size, as phones do.
            if (level != CompressLevel.Light)
                image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
        }

        if (level == CompressLevel.Strong && Math.Max(image.Width, image.Height) > StrongMaxEdge)
            image.Resize(new MagickGeometry(StrongMaxEdge, StrongMaxEdge)); // keeps the aspect ratio, only shrinks here

        image.Quality = Quality;
    }
}
