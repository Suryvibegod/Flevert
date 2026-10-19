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
        // Strip() also tells the PNG writer to exclude iCCP (and the other colour chunks), so a re-added profile would
        // silently not be written; replacing the list did not help (checked), only removing it does. The PNG writer then
        // writes leftover properties as text chunks, so clear them: Strip() already removed comments and dates, what is
        // left is decoder information (png:*, jpeg:*, icc:* descriptions).
        image.RemoveArtifact("png:exclude-chunk");
        foreach (var name in image.AttributeNames.ToList())
            image.RemoveAttribute(name);
        if (colourProfile is not null)
            image.SetProfile(colourProfile);
    }
}
