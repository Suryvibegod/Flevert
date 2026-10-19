using ImageMagick;

namespace ConvertAnything.Image;

internal static class ImageMetadata
{
    /// <summary>
    /// Removes EXIF (with GPS), XMP, IPTC and comments but keeps the ICC profile, which changes how the colours look.
    /// Call after auto-orienting, since the orientation lives in the EXIF that is removed.
    /// </summary>
    public static void StripExceptColourProfile(IMagickImage<byte> image)
    {
        var colourProfile = image.GetColorProfile();
        image.Strip();
        if (colourProfile is not null)
            image.SetProfile(colourProfile);
    }
}
