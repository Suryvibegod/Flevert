using System.Text;
using ImageMagick;
using Flevert.Image;

namespace Flevert.Pdf.Tests;

/// <summary>JPEGs for the tests, written by ImageMagick or assembled marker by marker.</summary>
internal static class TestJpeg
{
    public static readonly MagickColor Red = new(255, 0, 0);
    public static readonly MagickColor Blue = new(0, 0, 255);

    /// <summary>
    /// A <paramref name="width"/> x <paramref name="height"/> picture as ImageMagick writes it, with no metadata unless
    /// <paramref name="configure"/> adds some: a gradient (so the entropy-coded data is not trivial), a red block in the
    /// top-left quarter and a blue one in the bottom-right quarter, as stored (before any EXIF orientation).
    /// </summary>
    public static byte[] Make(uint width = 96, uint height = 64, Action<MagickImage>? configure = null, uint quality = 90)
    {
        using var image = Picture(width, height);
        image.Strip();
        image.Quality = quality;
        configure?.Invoke(image);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>The same picture as <see cref="Make"/>, as pixels.</summary>
    public static MagickImage Picture(uint width, uint height)
    {
        MagickSetup.EnsureInitialized();
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (int)(((y * width) + x) * 3);
                (rgb[at], rgb[at + 1], rgb[at + 2]) = (x < width / 2, y < height / 2) switch
                {
                    (true, true) => ((byte)255, (byte)0, (byte)0),
                    (false, false) => ((byte)0, (byte)0, (byte)255),
                    _ => ((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)128),
                };
            }
        }

        var image = new MagickImage();
        image.ReadPixels(rgb, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGB));
        return image;
    }

    /// <summary>EXIF with camera make, model and GPS position, as a phone writes them.</summary>
    public static ExifProfile PhoneExif(ushort orientation)
    {
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "SecretPhoneMake");
        exif.SetValue(ExifTag.Model, "SecretPhoneModel");
        exif.SetValue(ExifTag.Orientation, orientation);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(10, 1), new Rational(46, 1), new Rational(3000, 100)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(106, 1), new Rational(41, 1), new Rational(1000, 100)]);
        return exif;
    }

    /// <summary>Gives the image EXIF <paramref name="orientation"/>; ImageMagick writes the image's own orientation into the EXIF.</summary>
    public static void SetOrientation(MagickImage image, ushort orientation, ExifProfile? exif = null)
    {
        exif ??= new ExifProfile();
        exif.SetValue(ExifTag.Orientation, orientation);
        image.SetProfile(exif);
        image.Orientation = (Orientation)orientation;
    }

    /// <summary>A marker segment: FF, the marker, a two-byte big-endian length (itself included), the payload.</summary>
    public static byte[] Segment(byte marker, ReadOnlySpan<byte> payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    public static byte[] Segment(byte marker, string payload) => Segment(marker, Encoding.Latin1.GetBytes(payload));

    /// <summary><paramref name="jpeg"/> with <paramref name="segments"/> inserted right after SOI.</summary>
    public static byte[] InsertAfterSoi(byte[] jpeg, params byte[][] segments) =>
        [.. jpeg.AsSpan(0, 2), .. segments.SelectMany(s => s), .. jpeg.AsSpan(2)];

    /// <summary>
    /// An APP1 EXIF payload in either byte order: IFD0 holds Make ("SecretPhoneMake", stored after the IFD) then
    /// Orientation, so the orientation is not the first entry.
    /// </summary>
    public static byte[] ExifPayload(bool littleEndian, ushort orientation, uint? ifdOffset = null)
    {
        var tiff = new List<byte>();
        void U16(int v) => tiff.AddRange(littleEndian ? [(byte)v, (byte)(v >> 8)] : [(byte)(v >> 8), (byte)v]);
        void U32(uint v) => tiff.AddRange(littleEndian
            ? [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]
            : [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

        tiff.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42);
        U32(ifdOffset ?? 8);
        U16(2); // entries
        var make = "SecretPhoneMake\0"u8.ToArray();
        U16(0x010F); U16(2); U32((uint)make.Length); U32(8 + 2 + (2 * 12) + 4); // ASCII, stored after the IFD
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0); // SHORT, left-justified in the value field
        U32(0); // no next IFD
        tiff.AddRange(make);
        return [.. "Exif\0\0"u8, .. tiff];
    }

    /// <summary>
    /// A structurally valid JPEG that no decoder would accept (no tables): SOI, SOF0 for 16 x 8 pixels of one component,
    /// SOS, then <paramref name="entropy"/> and EOI. Enough for marker-level tests.
    /// </summary>
    public static byte[] Synthetic(ReadOnlySpan<byte> entropy, byte sof = 0xC0, byte precision = 8, int height = 8, byte components = 1)
    {
        var sofPayload = new List<byte> { precision, (byte)(height >> 8), (byte)height, 0, 16, components };
        for (var c = 0; c < components; c++)
            sofPayload.AddRange([(byte)(c + 1), 0x11, 0]);
        byte[] sos = [1, 1, 0x00, 0, 63, 0];
        return [0xFF, 0xD8, .. Segment(sof, [.. sofPayload]), .. Segment(0xDA, sos), .. entropy, 0xFF, 0xD9];
    }

    /// <summary>The bytes as text, to look for strings in binary data.</summary>
    public static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    public static MagickImage Decode(byte[] jpeg) => new(jpeg, new MagickReadSettings { Format = MagickFormat.Jpeg });

    /// <summary>Whether two JPEGs decode to exactly the same pixels.</summary>
    public static bool SamePixels(byte[] a, byte[] b)
    {
        using var first = Decode(a);
        using var second = Decode(b);
        return first.Width == second.Width && first.Height == second.Height && first.Compare(second, ErrorMetric.Absolute) == 0;
    }
}
