using Flevert.Core;
using ImageMagick;

namespace Flevert.Image;

/// <summary>The image operations offered in the menu, each waiting for its share of memory when files run in parallel.</summary>
public static class ImageOperations
{
    public static IReadOnlyList<IOperation> All { get; } = Unbudgeted().Select(op => (IOperation)new BudgetedOperation(op)).ToList();

    private static IOperation[] Unbudgeted() =>
    [
        new ImageConvert(".jpg", MagickFormat.Jpeg, quality: 92),
        new ImageConvert(".png", MagickFormat.Png),
        new ImageConvert(".webp", MagickFormat.WebP, quality: 90),
        new ImageConvert(".avif", MagickFormat.Avif, quality: 80),
        new ImageConvert(".gif", MagickFormat.Gif),
        new ImageConvert(".bmp", MagickFormat.Bmp),
        new ImageConvert(".tiff", MagickFormat.Tiff),
        new ImageCompress(CompressLevel.Light),
        new ImageCompress(CompressLevel.Balanced),
        new ImageCompress(CompressLevel.Strong),
        new ImageCompressUnder("image.compress-under-500kb", 500_000, "500 KB"),
        new ImageCompressUnder("image.compress-under-1mb", 1_000_000, "1 MB"),
        new ImageCompressUnder("image.compress-under-5mb", 5_000_000, "5 MB"),
        new ImageStripMetadata(),
    ];
}
