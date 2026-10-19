using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image;

/// <summary><c>Convert/&lt;FORMAT&gt;</c>: writes the image in another format beside the original.</summary>
public sealed class ImageConvert(string extension, MagickFormat format) : IOperation
{
    public string Id => $"image.to-{extension.TrimStart('.')}";

    public string MenuPath => $"Convert/{extension.TrimStart('.').ToUpperInvariant()}";

    public IReadOnlyCollection<string> Extensions { get; } =
        [.. ImageFile.ReadableExtensions.Where(e => ImageFile.FormatOf(e) != format)];

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + extension);
        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => Task.Run(() =>
        {
            using var images = ImageFile.Read(inputPath);
            var image = images[0];
            // Rotate the pixels so viewers that ignore EXIF orientation still show the image upright.
            image.AutoOrient();
            image.Write(temp, format);
        }, token), ct);
    }
}
