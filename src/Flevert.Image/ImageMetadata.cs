using ImageMagick;

namespace Flevert.Image;

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
        // The JPEG writer reads the source's chroma subsampling from this decoder attribute; without it, it picks 4:4:4
        // at quality >= 90 (a 4:2:0 phone photo grows) and 4:2:0 below (a 4:4:4 source loses chroma detail).
        if (image.GetAttribute("jpeg:sampling-factor") is { Length: > 0 } sampling)
            image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", sampling);
        foreach (var name in image.AttributeNames.ToList())
            image.RemoveAttribute(name);
        if (colourProfile is not null)
            image.SetProfile(colourProfile);
    }

    /// <summary>
    /// Converts a CMYK image to sRGB through its own CMYK profile (or a common press profile when it has none).
    /// Converting the numbers without a profile shifts colours by tens of levels.
    /// </summary>
    public static void ToSrgb(IMagickImage<byte> image)
    {
        if (image.ColorSpace != ColorSpace.CMYK)
            return;
        image.TransformColorSpace(image.GetColorProfile() ?? ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
    }

    /// <summary>
    /// Rotates the pixels upright from the EXIF orientation, so viewers that ignore EXIF show the image correctly. After
    /// a quarter turn, a JPEG's chroma subsampling turns with it (4:2:2 "2x1" becomes "1x2"), or it would halve the colour
    /// resolution in both directions once re-encoded.
    /// </summary>
    public static void AutoOrient(IMagickImage<byte> image)
    {
        var quarterTurn = image.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom;
        image.AutoOrient();
        if (quarterTurn && image.GetAttribute("jpeg:sampling-factor") is { Length: > 0 } sampling)
        {
            var turned = string.Join(',', sampling.Split(',').Select(f => f.Split('x') is [var h, var v] ? $"{v}x{h}" : f));
            image.SetAttribute("jpeg:sampling-factor", turned);
        }
    }

    /// <summary>
    /// A photo from a lossy 4:2:0 format (HEIC/HEIF/AVIF, lossy WebP) written as JPG keeps 4:2:0 chroma subsampling: at
    /// quality >= 90 ImageMagick would otherwise pick 4:4:4, and an iPhone photo grew to 144%, a WebP from 131 to 441 KB.
    /// </summary>
    public static void PhotoSubsamplingForJpeg(IMagickImage<byte> image, bool lossyPhotoSource, MagickFormat target)
    {
        if (target == MagickFormat.Jpeg && lossyPhotoSource)
            image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
    }

    /// <summary>Replaces transparency with white, for targets without an alpha channel (JPG, BMP).</summary>
    public static void FlattenOntoWhite(IMagickImage<byte> image)
    {
        if (!image.HasAlpha)
            return;
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
    }
}
