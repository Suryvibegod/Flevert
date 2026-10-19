using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image;

/// <summary>The image operations offered in the menu.</summary>
public static class ImageOperations
{
    public static IReadOnlyList<IOperation> All { get; } =
    [
        new ImageConvert(".png", MagickFormat.Png),
    ];
}
