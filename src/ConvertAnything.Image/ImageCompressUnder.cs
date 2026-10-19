using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image;

/// <summary>
/// <c>Compress/Under N</c>: a JPG strictly smaller than <paramref name="limitBytes"/>, at the best quality between 40 and
/// 92 that fits; if even quality 40 is too large, the image shrinks by 15% and the search repeats.
/// </summary>
/// <param name="label">Shown in the menu and the output name, e.g. <c>1 MB</c>; 1 MB is 1 000 000 bytes.</param>
public sealed class ImageCompressUnder(long limitBytes, string label) : IOperation
{
    private const uint MinQuality = 40;
    private const uint MaxQuality = 92;
    private const uint MinEdge = 16;

    public string Id => $"image.compress-under-{label.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()}";

    public string MenuPath => $"Compress/Under {label}";

    public IReadOnlyCollection<string> Extensions { get; } = [.. ImageFile.ReadableExtensions];

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var desired = Path.Combine(
            Path.GetDirectoryName(inputPath)!, $"{Path.GetFileNameWithoutExtension(inputPath)} (under {label}).jpg");
        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => Task.Run(() =>
        {
            using var images = ImageFile.Read(inputPath);
            var image = images[0];
            image.AutoOrient();
            ImageMetadata.StripExceptColourProfile(image);
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
            }

            File.WriteAllBytes(temp, Fit(image, token));
        }, token), ct);
    }

    private byte[] Fit(IMagickImage<byte> image, CancellationToken ct)
    {
        while (true)
        {
            // Binary search for the highest quality whose encoding is below the limit.
            byte[]? best = null;
            uint low = MinQuality, high = MaxQuality;
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

            if (best is not null)
                return best;

            if (Math.Min(image.Width, image.Height) * 85 / 100 < MinEdge)
                throw new InvalidOperationException($"The image cannot be made smaller than {label}.");
            image.Resize(new Percentage(85));
        }
    }

    private static byte[] Encode(IMagickImage<byte> image, uint quality)
    {
        image.Quality = quality;
        return image.ToByteArray(MagickFormat.Jpeg);
    }
}
