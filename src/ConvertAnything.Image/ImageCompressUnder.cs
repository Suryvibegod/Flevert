using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image;

/// <summary>
/// <c>Compress/Under N</c>: a JPG strictly smaller than <paramref name="limitBytes"/>, at the best quality between 40 and
/// 92 that fits; if even quality 40 is too large, the image is scaled down by what that attempt shows is needed and the
/// search repeats.
/// </summary>
/// <param name="id">Stable operation id; never derived from <paramref name="label"/>, which is display text.</param>
/// <param name="label">Shown in the menu and the output name, e.g. <c>1 MB</c>; 1 MB is 1 000 000 bytes.</param>
public sealed class ImageCompressUnder(string id, long limitBytes, string label) : IOperation
{
    private const uint MinQuality = 40;
    private const uint MaxQuality = 92;
    private const uint MinEdge = 16;

    // Even noise rarely needs more than this many bytes per pixel at quality 40, so starting above limit × 20 pixels only
    // spends time encoding sizes that cannot fit (a 108 MP photo would otherwise run into ImageMagick's time limit).
    private const double MaxStartPixelsPerByte = 20;

    // A profile this large (CMYK print profiles reach hundreds of KB) leaves too little room for the pixels.
    private const double MaxProfileShareOfLimit = 0.1;

    public string Id => id;

    public string MenuPath => $"Compress/Under {label}";

    public IReadOnlyCollection<string> Extensions { get; } = [.. ImageFile.ReadableExtensions];

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var desired = Path.Combine(
            Path.GetDirectoryName(inputPath)!, $"{Path.GetFileNameWithoutExtension(inputPath)} (under {label}).jpg");
        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => ImageFile.RunAsync(() =>
        {
            using var images = ImageFile.Read(inputPath, allFrames: false); // the result is one JPG
            ImageFile.StopOnCancel(images, token);
            var original = images[0];
            ImageMetadata.AutoOrient(original);
            ImageMetadata.StripExceptColourProfile(original);
            ToSrgbIfProfileTooLarge(original);
            ImageMetadata.FlattenOntoWhite(original);

            File.WriteAllBytes(temp, Fit(original, token));
        }, token), ct);
    }

    private void ToSrgbIfProfileTooLarge(IMagickImage<byte> image)
    {
        var profile = image.GetColorProfile();
        if (image.ColorSpace == ColorSpace.CMYK || (profile is not null && profile.ToByteArray().Length > limitBytes * MaxProfileShareOfLimit))
        {
            if (image.ColorSpace == ColorSpace.CMYK)
                ImageMetadata.ToSrgb(image);
            else
                image.TransformColorSpace(profile!, ColorProfiles.SRGB);
            image.RemoveProfile("icc"); // sRGB is what viewers assume without a profile
        }
    }

    private byte[] Fit(IMagickImage<byte> original, CancellationToken ct)
    {
        var pixels = (double)original.Width * original.Height;
        var scale = Math.Min(1, Math.Sqrt(limitBytes * MaxStartPixelsPerByte / pixels));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Always from the original: shrinking an already shrunk copy again would blur it a little more each round.
            using var work = original.Clone();
            ImageFile.StopOnCancel([work], ct); // its own callback; the clone would otherwise call the original's
            if (scale < 1)
                work.Resize(new Percentage(scale * 100));

            // One cheap probe first: if even the lowest quality does not fit, scale down by what it shows is needed.
            var lowest = Encode(work, MinQuality);
            if (lowest.Length >= limitBytes)
            {
                if (Math.Min(work.Width, work.Height) <= MinEdge)
                    throw new InvalidOperationException($"The image cannot be made smaller than {label}.");
                scale *= Math.Min(0.85, Math.Sqrt((double)limitBytes / lowest.Length) * 0.95);
                continue;
            }

            return BestQuality(work, lowest, ct);
        }
    }

    /// <summary>Binary search for the highest quality whose encoding is below the limit; quality 40 is known to fit.</summary>
    private byte[] BestQuality(IMagickImage<byte> image, byte[] fitsAtMin, CancellationToken ct)
    {
        var best = fitsAtMin;
        uint low = MinQuality + 1, high = MaxQuality;
        while (low <= high)
        {
            ct.ThrowIfCancellationRequested();
            var quality = (low + high) / 2;
            var encoded = Encode(image, quality);
            if (encoded.Length < limitBytes)
            {
                best = encoded;
                low = quality + 1;
            }
            else
            {
                high = quality - 1;
            }
        }

        return best;
    }

    private static byte[] Encode(IMagickImage<byte> image, uint quality)
    {
        image.Quality = quality;
        image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
        return image.ToByteArray(MagickFormat.Jpeg);
    }
}
