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
/// <para>
/// Kept, an allow-list: SOI; the first JFIF APP0, without its thumbnail (which can show what was cropped out);
/// APP2 holding an ICC profile; the 12 bytes of the first APP14 "Adobe" (it says how CMYK and YCCK colours are stored);
/// the tables (DQT, DHT), frame and scan headers and restart interval (DRI); the entropy-coded data with its stuffed
/// bytes and restart markers, up to EOI. Removed: COM and every other application segment, since cameras put metadata
/// in many of them (APP1 EXIF and XMP, APP13 IPTC, APP3 "Meta", APP12, APP2 MPF, APP0 JFXX thumbnails and Canon CIFF),
/// and everything after EOI (phones append further JPEGs there, each with its own EXIF). Any other marker fails.
/// </para>
/// <para>
/// pdfium only reads a JPEG's header before embedding it, so a missing table shows only when the page is drawn: without
/// a quantization table libjpeg refuses to decode and pdfium draws the image black; without a Huffman table libjpeg
/// uses the standard ones, garbage for an optimized JPEG, and decoders report a warning at most. So every
/// table a scan uses must be defined before it, or the JPEG is refused as damaged (<see cref="InvalidDataException"/>):
/// re-encoding would only turn the garbage into pixels.
/// </para>
/// </remarks>
internal static class JpegSegments
{
    /// <summary>The largest JPEG embedded: a 100 MP photo at quality 100 is about 60 MB.</summary>
    public const int MaxBytes = 256 * 1024 * 1024;

    /// <summary>
    /// The most scans a JPEG may have. A viewer goes over the whole image again for each progressive scan, so thousands
    /// of tiny scans make a small file take minutes to show (libjpeg-turbo offers TJPARAM_SCANLIMIT against it, off by
    /// default); libjpeg writes about ten.
    /// </summary>
    public const int MaxScans = 1_000;

    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sos = 0xDA;
    private const byte Dqt = 0xDB;
    private const byte Dht = 0xC4;
    private const byte Dri = 0xDD;
    private const byte Dac = 0xCC;
    private const byte Dhp = 0xDE;
    private const byte Exp = 0xDF;
    private const byte App0 = 0xE0;
    private const byte App1 = 0xE1;
    private const byte App2 = 0xE2;
    private const byte App14 = 0xEE;
    private const byte App15 = 0xEF;
    private const byte Com = 0xFE;

    /// <summary>"JFIF\0", version, units, X and Y density, then the thumbnail's width and height.</summary>
    private const int JfifHeaderLength = 14;

    /// <summary>"Adobe", version, two flag words and the colour transform.</summary>
    private const int AdobeLength = 12;

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
        Frame? frame = null;
        var scans = 0;
        var inScan = false;
        var jfif = false;
        var adobe = false;
        var tables = new Tables();

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
                    return new JpegImage(output[..written], frame!.Width, frame.Height, orientation == 0 ? 1 : orientation);
                case >= 0xD0 and <= 0xD7:
                    Copy(jpeg[markerStart..pos]); // RSTn stands alone, without a length
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

            switch (marker)
            {
                case App0:
                    // Only the first JFIF header, and never its thumbnail: copied as it is when it has none.
                    if (!jfif && payload.Length >= JfifHeaderLength && payload.StartsWith("JFIF\0"u8))
                    {
                        jfif = true;
                        if (payload.Length == JfifHeaderLength && payload[12] == 0 && payload[13] == 0)
                            Copy(jpeg[markerStart..end]);
                        else
                            Copy([0xFF, App0, 0, JfifHeaderLength + 2, .. payload[..12], 0, 0]);
                    }

                    break;
                case App1:
                    if (orientation == 0 && payload.StartsWith("Exif\0\0"u8))
                        orientation = ReadOrientation(payload[6..]);
                    break;
                case App2:
                    if (payload.StartsWith("ICC_PROFILE\0"u8))
                        Copy(jpeg[markerStart..end]);
                    break;
                case App14:
                    if (!adobe && payload.Length >= AdobeLength && payload.StartsWith("Adobe"u8))
                    {
                        adobe = true;
                        Copy([0xFF, App14, 0, AdobeLength + 2, .. payload[..AdobeLength]]);
                    }

                    break;
                case (> App2 and <= App15) or Com:
                    break; // metadata
                case Dqt:
                    tables.ReadQuantization(payload);
                    Copy(jpeg[markerStart..end]);
                    break;
                case Dht:
                    tables.ReadHuffman(payload);
                    Copy(jpeg[markerStart..end]);
                    break;
                case Dri:
                    if (payload.Length != 2)
                        throw new InvalidDataException("The JPEG is damaged: its restart interval has the wrong length.");
                    Copy(jpeg[markerStart..end]);
                    break;
                case Sos:
                    if (frame is null)
                        throw new InvalidDataException("The JPEG is damaged: its image data comes before its frame header.");
                    if (frame.Components.Length == 4 && !adobe)
                    {
                        // pdfium writes /Decode [1 0 1 0 1 0 1 0] for every CMYK JPEG, right only for Adobe's inverted CMYK.
                        throw new NotSupportedException("The JPEG is CMYK without the Adobe marker that says how its colours are stored.");
                    }

                    tables.CheckScan(payload, frame);
                    if (++scans > MaxScans)
                    {
                        throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                            $"The JPEG is damaged or hostile: it has more than {MaxScans} scans."));
                    }

                    inScan = true;
                    Copy(jpeg[markerStart..end]);
                    break;
                case Dac:
                    throw new NotSupportedException("The JPEG uses arithmetic coding, which PDF viewers cannot show.");
                case Dhp or Exp:
                    throw new NotSupportedException("The JPEG is hierarchical, which PDF viewers cannot show.");
                case var _ when IsFrameHeader(marker):
                    if (frame is not null)
                        throw new NotSupportedException("The JPEG holds several frames, which PDF viewers cannot show.");
                    frame = ReadFrame(marker, payload);
                    Copy(jpeg[markerStart..end]);
                    break;
                default:
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                        $"The JPEG is damaged or unusual: it holds marker FF{marker:X2}, which has no place in a photo."));
            }

            pos = end;
        }
    }

    private static bool IsRestart(byte marker) => marker is >= 0xD0 and <= 0xD7;

    // SOF0 to SOF15, except DHT (C4), JPG (C8) and DAC (CC), which share the range.
    private static bool IsFrameHeader(byte marker) => marker is >= 0xC0 and <= 0xCF and not (Dht or 0xC8 or Dac);

    private sealed record Frame(int Width, int Height, bool Progressive, (byte Id, byte Table)[] Components);

    private static Frame ReadFrame(byte marker, ReadOnlySpan<byte> payload)
    {
        // Baseline, extended and progressive Huffman coding are what PDF's DCTDecode and its viewers support.
        if (marker is not (0xC0 or 0xC1 or 0xC2))
        {
            throw new NotSupportedException(marker is 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF
                ? "The JPEG uses arithmetic coding, which PDF viewers cannot show."
                : "The JPEG is lossless or hierarchical, which PDF viewers cannot show.");
        }

        if (payload.Length < 6 || payload.Length != 6 + (3 * payload[5]))
            throw new InvalidDataException("The JPEG is damaged: its frame header has the wrong length.");
        var precision = payload[0];
        var height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
        var width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
        var count = payload[5];
        if (width == 0)
            throw new InvalidDataException("The JPEG is damaged: its width is zero.");
        if (precision != 8)
            throw new NotSupportedException("The JPEG stores more than 8 bits per sample, which PDF viewers cannot show.");
        if (count is not (1 or 3 or 4))
            throw new NotSupportedException("The JPEG has neither 1, 3 nor 4 colour components.");
        if (height == 0)
            throw new NotSupportedException("The JPEG gives its height only after the image data, which PDF viewers cannot show.");

        var components = new (byte Id, byte Table)[count];
        for (var i = 0; i < count; i++)
        {
            var table = payload[6 + (3 * i) + 2];
            if (table > 3)
                throw new InvalidDataException("The JPEG is damaged: its frame header names a quantization table past 3.");
            components[i] = (payload[6 + (3 * i)], table);
        }

        return new Frame(width, height, marker == 0xC2, components);
    }

    /// <summary>Which quantization and Huffman tables have been defined so far (four of each kind, as libjpeg allows).</summary>
    private sealed class Tables
    {
        private readonly bool[] _quantization = new bool[4];
        private readonly bool[] _dc = new bool[4];
        private readonly bool[] _ac = new bool[4];

        public void ReadQuantization(ReadOnlySpan<byte> payload)
        {
            while (!payload.IsEmpty)
            {
                var (precision, table) = (payload[0] >> 4, payload[0] & 0x0F);
                var size = precision == 0 ? 64 : 128; // 8-bit or 16-bit values
                if (precision > 1 || table > 3 || payload.Length < 1 + size)
                    throw new InvalidDataException("The JPEG is damaged: a quantization table is malformed.");
                _quantization[table] = true;
                payload = payload[(1 + size)..];
            }
        }

        public void ReadHuffman(ReadOnlySpan<byte> payload)
        {
            while (!payload.IsEmpty)
            {
                if (payload.Length < 17)
                    throw MalformedHuffman();
                var (kind, table) = (payload[0] >> 4, payload[0] & 0x0F);
                var codes = 0;
                foreach (var count in payload[1..17])
                    codes += count;
                if (kind > 1 || table > 3 || codes > 256 || payload.Length < 17 + codes)
                    throw MalformedHuffman();
                (kind == 0 ? _dc : _ac)[table] = true;
                payload = payload[(17 + codes)..];
            }
        }

        /// <summary>
        /// Checks a scan header against the frame and the tables defined before it. A sequential scan decodes with both
        /// Huffman tables of each component; a progressive DC scan with its DC table (none when refining), an AC scan
        /// with its AC table.
        /// </summary>
        public void CheckScan(ReadOnlySpan<byte> payload, Frame frame)
        {
            if (payload.IsEmpty || payload[0] is < 1 or > 4 || payload.Length != 1 + (2 * payload[0]) + 3)
                throw new InvalidDataException("The JPEG is damaged: a scan header has the wrong length.");
            var count = payload[0];
            var spectralStart = payload[1 + (2 * count)];
            var refining = payload[3 + (2 * count)] >> 4 != 0;
            var needsDc = !frame.Progressive || (spectralStart == 0 && !refining);
            var needsAc = !frame.Progressive || spectralStart != 0;
            for (var i = 0; i < count; i++)
            {
                var id = payload[1 + (2 * i)];
                var index = 0;
                while (index < frame.Components.Length && frame.Components[index].Id != id)
                    index++;
                if (index == frame.Components.Length)
                    throw new InvalidDataException("The JPEG is damaged: a scan names a colour component its frame does not have.");
                if (!_quantization[frame.Components[index].Table])
                    throw new InvalidDataException("The JPEG is damaged: it uses a quantization table it never defines.");
                var (dc, ac) = (payload[2 + (2 * i)] >> 4, payload[2 + (2 * i)] & 0x0F);
                if ((needsDc && (dc > 3 || !_dc[dc])) || (needsAc && (ac > 3 || !_ac[ac])))
                    throw new InvalidDataException("The JPEG is damaged: it uses a Huffman table it never defines.");
            }
        }

        private static InvalidDataException MalformedHuffman() => new("The JPEG is damaged: a Huffman table is malformed.");
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
