using System.Buffers.Binary;
using System.Globalization;

namespace Flevert.Pdf;

/// <summary>A JPEG ready to embed in a PDF as it is: metadata removed, what decoding needs left byte for byte.</summary>
internal sealed class JpegImage
{
    internal JpegImage(byte[] bytes, int width, int height, int orientation)
    {
        Bytes = bytes;
        Width = width;
        Height = height;
        Orientation = orientation;
    }

    /// <summary>The JPEG, from SOI to EOI.</summary>
    public byte[] Bytes { get; }

    /// <summary>The size stored in the frame header, before <see cref="Orientation"/> is applied.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>The EXIF orientation (1 to 8) the image had; 1 when it had none or an invalid one.</summary>
    public int Orientation { get; }
}

/// <summary>
/// Removes a JPEG's metadata at the marker level, without decoding it: embedded in a PDF unchanged, a photo keeps its
/// exact quality and loses its GPS position (docs/specs/pdf.md, "Ảnh → PDF"). JPEGs are untrusted input: every length
/// is checked against the data, the walk always moves forward, and anything unexpected fails with
/// <see cref="InvalidDataException"/>, or <see cref="NotSupportedException"/> for a valid JPEG that PDF viewers cannot
/// show (arithmetic, lossless or hierarchical coding, 12-bit samples), which can still be decoded and re-encoded.
/// </summary>
/// <remarks>
/// Kept: SOI, APP0 (JFIF), APP2 holding an ICC profile, APP14 "Adobe" (it says how CMYK and YCCK colours are stored),
/// every non-application marker (tables, frame and scan headers, restart intervals) and the entropy-coded data with its
/// stuffed bytes and restart markers, up to EOI. Removed: COM and every other application segment, an allow-list rather
/// than a deny-list, since cameras put metadata in many of them (APP1 EXIF and XMP, APP13 IPTC, APP3 "Meta", APP12,
/// APP2 MPF), and everything after EOI (phones append further JPEGs there, each with its own EXIF).
/// </remarks>
internal static class JpegSegments
{
    /// <summary>The largest JPEG embedded: a 100 MP photo at quality 100 is about 60 MB.</summary>
    public const int MaxBytes = 256 * 1024 * 1024;

    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sos = 0xDA;
    private const byte App0 = 0xE0;
    private const byte App2 = 0xE2;
    private const byte App14 = 0xEE;
    private const byte App15 = 0xEF;
    private const byte Com = 0xFE;
    private const byte App1 = 0xE1;

    /// <inheritdoc cref="Strip(ReadOnlySpan{byte}, int)"/>
    public static JpegImage Strip(ReadOnlySpan<byte> jpeg) => Strip(jpeg, MaxBytes);

    /// <summary>
    /// The JPEG without its metadata, with its size and EXIF orientation. Throws <see cref="InvalidDataException"/> for
    /// a damaged JPEG and <see cref="NotSupportedException"/> for one PDF viewers cannot show.
    /// </summary>
    internal static JpegImage Strip(ReadOnlySpan<byte> jpeg, int maxBytes)
    {
        if (jpeg.Length > maxBytes)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"The JPEG is too large: {jpeg.Length} bytes, and Flevert embeds at most {maxBytes}."));
        }

        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != Soi)
            throw new InvalidDataException("The file is not a JPEG.");

        var output = new byte[jpeg.Length];
        var written = 0;
        void Copy(ReadOnlySpan<byte> part)
        {
            part.CopyTo(output.AsSpan(written));
            written += part.Length;
        }

        Copy(jpeg[..2]);
        var pos = 2;
        var orientation = 0; // not seen yet
        (int Width, int Height)? frame = null;
        var scans = 0;
        var inScan = false;

        while (true)
        {
            if (inScan)
            {
                // Entropy-coded data runs to the next marker. FF is followed by 00 (a stuffed byte), by RSTn, or by
                // further FF fill bytes before a marker; all of it is kept as it is.
                var start = pos;
                while (true)
                {
                    var found = jpeg[pos..].IndexOf((byte)0xFF);
                    if (found < 0)
                        throw Truncated();
                    var next = pos + found + 1;
                    while (next < jpeg.Length && jpeg[next] == 0xFF)
                        next++;
                    if (next >= jpeg.Length)
                        throw Truncated();
                    if (jpeg[next] == 0x00 || IsRestart(jpeg[next]))
                    {
                        pos = next + 1;
                        continue;
                    }

                    pos = next - 1; // the FF just before the marker
                    break;
                }

                Copy(jpeg[start..pos]);
                inScan = false;
            }

            if (pos >= jpeg.Length)
                throw Truncated();
            if (jpeg[pos] != 0xFF)
                throw Damaged(pos);
            var markerStart = pos;
            while (pos < jpeg.Length && jpeg[pos] == 0xFF)
                pos++; // fill bytes may precede any marker
            if (pos >= jpeg.Length)
                throw Truncated();
            var marker = jpeg[pos++];

            switch (marker)
            {
                case 0x00:
                    throw Damaged(markerStart);
                case Soi:
                    throw new InvalidDataException("The JPEG is damaged: it starts a second time inside itself.");
                case Eoi:
                    if (scans == 0)
                        throw new InvalidDataException("The JPEG holds no image data.");
                    Copy(jpeg[markerStart..pos]);
                    return new JpegImage(output[..written], frame!.Value.Width, frame.Value.Height, orientation == 0 ? 1 : orientation);
                case 0x01 or (>= 0xD0 and <= 0xD7):
                    Copy(jpeg[markerStart..pos]); // TEM and RSTn stand alone, without a length
                    continue;
            }

            if (pos + 2 > jpeg.Length)
                throw Truncated();
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[pos..]);
            if (length < 2)
                throw Damaged(markerStart);
            var end = pos + length;
            if (end > jpeg.Length)
                throw Truncated();
            var payload = jpeg[(pos + 2)..end];

            if (marker == App1 && orientation == 0 && payload.StartsWith("Exif\0\0"u8))
                orientation = ReadOrientation(payload[6..]);
            if (IsFrameHeader(marker))
            {
                if (frame is not null)
                    throw new NotSupportedException("The JPEG holds several frames, which PDF viewers cannot show.");
                frame = ReadFrame(marker, payload);
            }

            if (marker == Sos)
            {
                if (frame is null)
                    throw new InvalidDataException("The JPEG is damaged: its image data comes before its frame header.");
                scans++;
                inScan = true;
            }

            if (Keep(marker, payload))
                Copy(jpeg[markerStart..end]);
            pos = end;
        }
    }

    private static bool Keep(byte marker, ReadOnlySpan<byte> payload) => marker switch
    {
        App0 => true,
        App2 => payload.StartsWith("ICC_PROFILE\0"u8),
        App14 => payload.StartsWith("Adobe"u8),
        >= App0 and <= App15 => false,
        Com => false,
        _ => true,
    };

    private static bool IsRestart(byte marker) => marker is >= 0xD0 and <= 0xD7;

    // SOF0 to SOF15, except DHT (C4), JPG (C8) and DAC (CC), which share the range.
    private static bool IsFrameHeader(byte marker) => marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);

    private static (int Width, int Height) ReadFrame(byte marker, ReadOnlySpan<byte> payload)
    {
        // Baseline, extended and progressive Huffman coding are what PDF's DCTDecode and its viewers support.
        if (marker is not (0xC0 or 0xC1 or 0xC2))
        {
            throw new NotSupportedException(marker is 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF
                ? "The JPEG uses arithmetic coding, which PDF viewers cannot show."
                : "The JPEG is lossless or hierarchical, which PDF viewers cannot show.");
        }

        if (payload.Length < 6 || payload.Length < 6 + (3 * payload[5]))
            throw new InvalidDataException("The JPEG is damaged: its frame header is too short.");
        var precision = payload[0];
        var height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
        var width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
        var components = payload[5];
        if (width == 0)
            throw new InvalidDataException("The JPEG is damaged: its width is zero.");
        if (precision != 8)
            throw new NotSupportedException("The JPEG stores more than 8 bits per sample, which PDF viewers cannot show.");
        if (components is not (1 or 3 or 4))
            throw new NotSupportedException("The JPEG has neither 1, 3 nor 4 colour components.");
        if (height == 0)
            throw new NotSupportedException("The JPEG gives its height only after the image data, which PDF viewers cannot show.");
        return (width, height);
    }

    /// <summary>The Orientation tag (0x0112) of IFD0 in a TIFF structure; 1 when missing, damaged or out of range.</summary>
    private static int ReadOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
            return 1;
        bool little;
        if (tiff.StartsWith("II"u8))
            little = true;
        else if (tiff.StartsWith("MM"u8))
            little = false;
        else
            return 1;
        if (U16(tiff, 2, little) != 42)
            return 1;
        var ifd = U32(tiff, 4, little);
        if (ifd < 8 || ifd > (uint)tiff.Length - 2)
            return 1;
        var count = U16(tiff, (int)ifd, little);
        for (var i = 0; i < count; i++)
        {
            var entry = (long)ifd + 2 + (12L * i);
            if (entry + 12 > tiff.Length)
                return 1;
            var at = (int)entry;
            if (U16(tiff, at, little) != 0x0112)
                continue;
            var value = (U16(tiff, at + 2, little), U32(tiff, at + 4, little)) switch
            {
                (3, >= 1) => U16(tiff, at + 8, little), // SHORT, as the standard says
                (4, >= 1) => U32(tiff, at + 8, little), // LONG, which some writers use
                _ => 0u,
            };
            return value is >= 1 and <= 8 ? (int)value : 1;
        }

        return 1;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private static uint U32(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

    private static InvalidDataException Truncated() => new("The JPEG is damaged: it ends too early.");

    private static InvalidDataException Damaged(int offset) => new(string.Create(CultureInfo.InvariantCulture,
        $"The JPEG is damaged: no marker where one should be, at byte {offset}."));
}
