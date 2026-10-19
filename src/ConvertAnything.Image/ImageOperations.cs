using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image;

/// <summary>The image operations offered in the menu.</summary>
public static class ImageOperations
{
    public static IReadOnlyList<IOperation> All { get; } =
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
    ];
}
