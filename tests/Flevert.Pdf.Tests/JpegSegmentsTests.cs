using System.Diagnostics;
using ImageMagick;
using Flevert.Image;

namespace Flevert.Pdf.Tests;

public class JpegSegmentsTests
{
    private static readonly string[] Secrets =
        ["SecretPhoneMake", "SecretPhoneModel", "SecretXmpCreator", "SecretIptcByline", "SecretComment"];

    private static byte[] JpegWithEverything(ushort orientation = 1) => TestJpeg.Make(configure: image =>
    {
        TestJpeg.SetOrientation(image, orientation, TestJpeg.PhoneExif(orientation));
        image.SetProfile(new XmpProfile("<x:xmpmeta xmlns:x='adobe:ns:meta/'><Creator>SecretXmpCreator</Creator></x:xmpmeta>"u8.ToArray()));
        var iptc = new IptcProfile();
        iptc.SetValue(IptcTag.Byline, "SecretIptcByline");
        image.SetProfile(iptc);
        image.Comment = "SecretComment";
        image.SetProfile(ColorProfiles.SRGB);
    });

    [Fact]
    public void Removes_exif_xmp_iptc_and_comments_but_keeps_the_colour_profile()
    {
        var original = JpegWithEverything();
        var text = TestJpeg.Latin1(original);
        // Control: ImageMagick really wrote every kind of metadata the test expects to disappear.
        foreach (var secret in Secrets)
            Assert.Contains(secret, text, StringComparison.Ordinal);
        Assert.Contains("Exif\0\0", text, StringComparison.Ordinal);
        Assert.Contains("http://ns.adobe.com/xap/1.0/", text, StringComparison.Ordinal);
        Assert.Contains("Photoshop 3.0", text, StringComparison.Ordinal);
        Assert.Contains("ICC_PROFILE", text, StringComparison.Ordinal);

        var stripped = JpegSegments.Strip(original).Bytes;

        var result = TestJpeg.Latin1(stripped);
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.DoesNotContain("Exif\0\0", result, StringComparison.Ordinal);
        Assert.DoesNotContain("http://ns.adobe.com/xap/1.0/", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Photoshop 3.0", result, StringComparison.Ordinal);
        using var decodedOriginal = TestJpeg.Decode(original);
        using var decoded = TestJpeg.Decode(stripped);
        Assert.Equal(decodedOriginal.GetColorProfile()!.ToByteArray(), decoded.GetColorProfile()?.ToByteArray());
        Assert.Null(decoded.GetExifProfile());
        Assert.True(TestJpeg.SamePixels(original, stripped), "the entropy-coded data must be unchanged");
    }

    [Fact]
    public void A_jpeg_without_metadata_comes_back_byte_for_byte()
    {
        var clean = TestJpeg.Make();
        Assert.Contains("JFIF\0", TestJpeg.Latin1(clean), StringComparison.Ordinal); // control: APP0 is there to keep

        var image = JpegSegments.Strip(clean);

        Assert.Equal(clean, image.Bytes);
        Assert.Equal((96, 64), (image.Width, image.Height));
        Assert.Equal(1, image.Orientation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Reads_the_exif_orientation_before_removing_it(ushort orientation)
    {
        var jpeg = JpegWithEverything(orientation);

        var image = JpegSegments.Strip(jpeg);

        Assert.Equal(orientation, image.Orientation);
        // The size is the stored one: the orientation is applied later, by the page.
        Assert.Equal((96, 64), (image.Width, image.Height));
        Assert.DoesNotContain("Exif\0\0", TestJpeg.Latin1(image.Bytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reads_the_orientation_in_either_byte_order(bool littleEndian)
    {
        var jpeg = TestJpeg.InsertAfterSoi(TestJpeg.Make(), TestJpeg.Segment(0xE1, TestJpeg.ExifPayload(littleEndian, 8)));

        var image = JpegSegments.Strip(jpeg);

        Assert.Equal(8, image.Orientation);
        Assert.Equal(TestJpeg.Make(), image.Bytes);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)9)]
    [InlineData((ushort)0xFFFF)]
    public void An_orientation_outside_1_to_8_counts_as_upright(ushort orientation)
    {
        var jpeg = TestJpeg.InsertAfterSoi(TestJpeg.Make(), TestJpeg.Segment(0xE1, TestJpeg.ExifPayload(littleEndian: true, orientation)));

        Assert.Equal(1, JpegSegments.Strip(jpeg).Orientation);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(0xFFFFFFF0u)]
    [InlineData(40u)]
    public void Damaged_exif_counts_as_upright_and_is_still_removed(uint ifdOffset)
    {
        var jpeg = TestJpeg.InsertAfterSoi(TestJpeg.Make(), TestJpeg.Segment(0xE1, TestJpeg.ExifPayload(littleEndian: false, 6, ifdOffset)));

        var image = JpegSegments.Strip(jpeg);

        Assert.Equal(1, image.Orientation);
        Assert.Equal(TestJpeg.Make(), image.Bytes);
    }

    [Fact]
    public void Exif_cut_short_inside_the_ifd_counts_as_upright()
    {
        var payload = TestJpeg.ExifPayload(littleEndian: true, 6);
        var jpeg = TestJpeg.InsertAfterSoi(TestJpeg.Make(), TestJpeg.Segment(0xE1, payload.AsSpan(0, 6 + 8 + 2 + 12 + 4)));

        Assert.Equal(1, JpegSegments.Strip(jpeg).Orientation);
    }

    [Fact]
    public void A_jpeg_without_exif_is_upright()
    {
        Assert.Equal(1, JpegSegments.Strip(TestJpeg.Make()).Orientation);
    }

    [Fact]
    public void Drops_other_application_segments_but_keeps_jfif_the_icc_profile_and_adobe()
    {
        var clean = TestJpeg.Make();
        var icc = TestJpeg.Segment(0xE2, "ICC_PROFILE\0\x01\x01" + "fake profile bytes");
        var adobe = TestJpeg.Segment(0xEE, "Adobe\0\x64\0\0\0\0\x01");
        var jpeg = TestJpeg.InsertAfterSoi(clean,
            TestJpeg.Segment(0xE2, "MPF\0SecretMultiPicture"),   // APP2 that is not a colour profile
            icc,
            TestJpeg.Segment(0xE3, "Meta\0SecretKodakMeta"),       // APP3: EXIF-like camera data
            TestJpeg.Segment(0xEC, "Ducky\0SecretDucky"),          // APP12
            TestJpeg.Segment(0xEB, "JP\0SecretJpegXt"),            // APP11
            TestJpeg.Segment(0xEF, "SecretApp15"),
            adobe);

        var stripped = JpegSegments.Strip(jpeg).Bytes;

        Assert.Equal(TestJpeg.InsertAfterSoi(clean, icc, adobe), stripped);
    }

    // JFIF APP0: "JFIF\0", version 1.02, units 1 (DPI), density 300 x 300, then the thumbnail's width and height.
    private static readonly byte[] JfifHeader = [.. "JFIF\0"u8, 1, 2, 1, 0x01, 0x2C, 0x01, 0x2C];

    public static TheoryData<string, byte[]> JfifWithExtras() => new()
    {
        // A 2 x 1 RGB thumbnail: it can show what was cropped out of the photo. Its colour is the secret here.
        { "a thumbnail", TestJpeg.Segment(0xE0, [.. JfifHeader, 2, 1, 0x5E, 0xC2, 0x7A, 0x5E, 0xC2, 0x7A]) },
        { "bytes after a 0 x 0 thumbnail", TestJpeg.Segment(0xE0, [.. JfifHeader, 0, 0, .. "SecretTrailer"u8]) },
    };

    [Theory]
    [MemberData(nameof(JfifWithExtras))]
    public void Keeps_the_jfif_header_but_not_its_thumbnail_or_anything_after_it(string what, byte[] app0)
    {
        var clean = TestJpeg.Make();
        var jpeg = TestJpeg.ReplaceApp0(clean, app0);

        var stripped = JpegSegments.Strip(jpeg).Bytes;

        // The same version, units and density, with no thumbnail: 16 bytes after the marker.
        byte[] header = [0xFF, 0xE0, 0x00, 0x10, .. JfifHeader, 0, 0];
        Assert.True(TestJpeg.ReplaceApp0(clean, header).SequenceEqual(stripped), what);
        Assert.Equal(-1, stripped.AsSpan().IndexOf([(byte)0x5E, (byte)0xC2, (byte)0x7A]));
        Assert.DoesNotContain("Secret", TestJpeg.Latin1(stripped), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("JFXX\0\x10SecretJfxxThumbnail")] // JFIF extension: a thumbnail as a JPEG, palette or RGB
    [InlineData("CIFF\0SecretOwnerNameAndSerial")] // Canon's camera data
    [InlineData("AVI1\0SecretAvi1")]                // motion-JPEG frames
    [InlineData("JFIF\0\x01")]                     // a JFIF header cut short
    public void Drops_every_other_app0_segment(string payload)
    {
        var clean = TestJpeg.Make();
        // After the JPEG's own JFIF APP0, as cameras write them; a second JFIF header is dropped too.
        byte[] jpeg = [.. clean.AsSpan(0, 20), .. TestJpeg.Segment(0xE0, payload), .. TestJpeg.Segment(0xE0, clean.AsSpan(6, 14)), .. clean.AsSpan(20)];
        Assert.Equal(0xDB, clean[21]); // control: the JFIF APP0 of an ImageMagick JPEG ends at byte 20

        Assert.Equal(clean, JpegSegments.Strip(jpeg).Bytes);
    }

    [Fact]
    public void Keeps_only_the_twelve_bytes_of_the_adobe_segment()
    {
        var clean = TestJpeg.Make();
        const string Adobe = "Adobe\0\x64\0\0\0\0\x01";
        var jpeg = TestJpeg.InsertAfterSoi(clean,
            TestJpeg.Segment(0xEE, Adobe + "SecretAdobeTail"),
            TestJpeg.Segment(0xEE, "Adobe\0\x64")); // too short to say anything: dropped

        var stripped = JpegSegments.Strip(jpeg).Bytes;

        Assert.Equal(TestJpeg.InsertAfterSoi(clean, TestJpeg.Segment(0xEE, Adobe)), stripped);
    }

    public static TheoryData<string, byte[]> MissingTables()
    {
        var bogusIndex = TestJpeg.Make();
        bogusIndex[bogusIndex.AsSpan().IndexOf([(byte)0xFF, (byte)0xDB]) + 4] = 0x0F;
        var synthetic = TestJpeg.Synthetic([0x00]);
        var sos = synthetic.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]);
        return new()
        {
            // pdfium embeds this one and renders it black (PdfBuilderTests).
            { "no quantization table", TestJpeg.WithoutSegments(TestJpeg.Make(), 0xDB) },
            // libjpeg falls back to the standard Huffman tables: decoders "succeed" with a warning and show garbage.
            { "no Huffman table", TestJpeg.WithoutSegments(TestJpeg.Make(), 0xC4) },
            { "a quantization table index past 3", bogusIndex },
            { "a scan of a component the frame lacks", Patch(synthetic, sos + 5, 2, 1) },
            { "a scan using Huffman tables 1", Patch(synthetic, sos + 6, 0x11, 1) },
            { "a Huffman table claiming more codes than it holds", Patch(synthetic, synthetic.AsSpan().IndexOf([(byte)0xFF, (byte)0xC4]) + 5, 9, 1) },
        };
    }

    [Theory]
    [MemberData(nameof(MissingTables))]
    public void Refuses_a_jpeg_whose_tables_are_missing_or_malformed(string what, byte[] jpeg)
    {
        var error = Assert.Throws<InvalidDataException>(() => JpegSegments.Strip(jpeg));
        Assert.Contains("damaged", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(what));
    }

    [Fact]
    public void Accepts_tables_defined_after_the_frame_header()
    {
        // Legal, and what TestJpeg.Synthetic does too: a table only has to come before the scan that uses it.
        var clean = TestJpeg.Make();
        var dqt = clean.AsSpan().IndexOf([(byte)0xFF, (byte)0xDB]);
        var dqtEnd = dqt + 2 + ((clean[dqt + 2] << 8) | clean[dqt + 3]);
        var sof = clean.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0]);
        var sofEnd = sof + 2 + ((clean[sof + 2] << 8) | clean[sof + 3]);
        Assert.True(dqtEnd <= sof, "control: ImageMagick writes the quantization tables before the frame header");
        byte[] jpeg = [.. clean.AsSpan(0, dqt), .. clean.AsSpan(dqtEnd, sofEnd - dqtEnd), .. clean.AsSpan(dqt, dqtEnd - dqt), .. clean.AsSpan(sofEnd)];

        var stripped = JpegSegments.Strip(jpeg).Bytes;

        Assert.Equal(jpeg, stripped);
        Assert.True(TestJpeg.SamePixels(clean, stripped));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0x01 })]                         // TEM
    [InlineData(new byte[] { 0xFF, 0x02, 0x00, 0x04, 0x00, 0x00 })] // reserved
    [InlineData(new byte[] { 0xFF, 0xC8, 0x00, 0x04, 0x00, 0x00 })] // JPG, reserved for extensions
    [InlineData(new byte[] { 0xFF, 0xDC, 0x00, 0x04, 0x00, 0x08 })] // DNL, without a frame of unknown height
    [InlineData(new byte[] { 0xFF, 0xF7, 0x00, 0x04, 0x00, 0x00 })] // JPEG-LS frame header (JPGn)
    [InlineData(new byte[] { 0xFF, 0xDD, 0x00, 0x05, 0x00, 0x01, 0x00 })] // DRI of the wrong length
    public void Refuses_markers_a_photo_for_a_pdf_has_no_use_for(byte[] segment)
    {
        var jpeg = TestJpeg.InsertAfterSoi(TestJpeg.Make(), segment);

        Assert.Throws<InvalidDataException>(() => JpegSegments.Strip(jpeg));
    }

    [Fact]
    public void A_cmyk_jpeg_without_its_adobe_segment_is_left_to_re_encoding()
    {
        // pdfium writes /Decode [1 0 1 0 1 0 1 0] for every CMYK JPEG, which is right only for Adobe's inverted CMYK:
        // without APP14 nothing says how the colours are stored.
        var cmyk = TestJpeg.Make(configure: image => image.ColorSpace = ColorSpace.CMYK);

        var error = Assert.Throws<NotSupportedException>(() => JpegSegments.Strip(TestJpeg.WithoutSegments(cmyk, 0xEE)));

        Assert.Contains("CMYK", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Removes_metadata_between_the_scans_of_a_progressive_jpeg()
    {
        var progressive = TestJpeg.Make(160, 120, image => image.Settings.Interlace = Interlace.Jpeg);
        var scans = Offsets(progressive, 0xDA);
        Assert.True(scans.Count > 1, "control: a progressive JPEG has several scans");
        // A COM and an EXIF APP1 before the second scan: markers may appear between scans.
        int second = scans[1];
        byte[] jpeg =
        [
            .. progressive.AsSpan(0, second),
            .. TestJpeg.Segment(0xFE, "SecretComment"),
            .. TestJpeg.Segment(0xE1, TestJpeg.ExifPayload(littleEndian: true, 3)),
            .. progressive.AsSpan(second),
        ];

        var image = JpegSegments.Strip(jpeg);

        Assert.Equal(progressive, image.Bytes);
        Assert.Equal((160, 120), (image.Width, image.Height));
        Assert.True(TestJpeg.SamePixels(progressive, image.Bytes));
    }

    [Fact]
    public void Keeps_the_adobe_segment_of_a_cmyk_jpeg()
    {
        var cmyk = TestJpeg.Make(configure: image =>
        {
            image.ColorSpace = ColorSpace.CMYK;
            image.Comment = "SecretComment";
        });
        var text = TestJpeg.Latin1(cmyk);
        Assert.Contains("Adobe", text, StringComparison.Ordinal); // control: libjpeg writes APP14 for CMYK
        Assert.Contains("SecretComment", text, StringComparison.Ordinal);

        var stripped = JpegSegments.Strip(cmyk).Bytes;

        Assert.Contains("Adobe", TestJpeg.Latin1(stripped), StringComparison.Ordinal);
        Assert.DoesNotContain("SecretComment", TestJpeg.Latin1(stripped), StringComparison.Ordinal);
        using var decoded = TestJpeg.Decode(stripped);
        Assert.Equal(ColorSpace.CMYK, decoded.ColorSpace);
        Assert.True(TestJpeg.SamePixels(cmyk, stripped));
    }

    [Fact]
    public void Keeps_restart_markers_fill_bytes_and_stuffed_bytes_in_the_entropy_coded_data()
    {
        byte[] entropy = [0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0xFF, 0xFF, 0xD1, 0x78, 0xFF, 0xD7, 0xFF, 0x00];
        var jpeg = TestJpeg.Synthetic(entropy);

        var image = JpegSegments.Strip(jpeg);

        Assert.Equal(jpeg, image.Bytes);
        Assert.Equal((16, 8), (image.Width, image.Height));
    }

    [Fact]
    public void Keeps_restart_markers_of_a_real_jpeg()
    {
        // A restart interval makes libjpeg write DRI and RSTn markers.
        var jpeg = TestJpeg.Make(configure: image => image.Settings.SetDefine(MagickFormat.Jpeg, "restart-interval", "1"));
        Assert.True(Offsets(jpeg, 0xDD).Count == 1, "control: the JPEG has a restart interval");

        var stripped = JpegSegments.Strip(jpeg).Bytes;

        Assert.Equal(jpeg, stripped);
    }

    [Fact]
    public void Drops_whatever_follows_the_end_of_the_image()
    {
        // Phones append further JPEGs (gain maps, depth maps) after EOI, each with its own EXIF.
        var clean = TestJpeg.Make();
        var appended = TestJpeg.Make(32, 32, image => TestJpeg.SetOrientation(image, 1, TestJpeg.PhoneExif(1)));
        Assert.Contains("SecretPhoneMake", TestJpeg.Latin1(appended), StringComparison.Ordinal);

        var image = JpegSegments.Strip([.. clean, .. appended]);

        Assert.Equal(clean, image.Bytes);
    }

    public static TheoryData<string, byte[]> Malformed()
    {
        var valid = TestJpeg.Make();
        var sos = Offsets(valid, 0xDA)[0];
        return new()
        {
            { "empty", [] },
            { "one byte", [0xFF] },
            { "not a JPEG", "GIF89a and more bytes"u8.ToArray() },
            { "SOI only", [0xFF, 0xD8] },
            { "SOI then EOI", [0xFF, 0xD8, 0xFF, 0xD9] },
            { "a length past the end", [0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x00, 0x4A, 0x46] },
            { "a length of one", [0xFF, 0xD8, 0xFF, 0xFE, 0x00, 0x01, 0xFF, 0xD9] },
            { "a length of zero", [0xFF, 0xD8, 0xFF, 0xFE, 0x00, 0x00, 0xFF, 0xD9] },
            { "a marker cut after FF", [0xFF, 0xD8, 0xFF] },
            { "a length cut in half", [0xFF, 0xD8, 0xFF, 0xDB, 0x00] },
            { "a byte that is not a marker", [0xFF, 0xD8, 0x12, 0x34, 0xFF, 0xD9] },
            { "a second SOI", [0xFF, 0xD8, 0xFF, 0xD8, 0xFF, 0xD9] },
            { "no SOS", [.. valid.AsSpan(0, sos), 0xFF, 0xD9] },
            { "no SOF before SOS", [0xFF, 0xD8, .. TestJpeg.Segment(0xDA, [1, 1, 0, 0, 63, 0]), 0x00, 0xFF, 0xD9] },
            { "no EOI", valid[..^2] },
            { "cut inside the entropy-coded data", valid[..(sos + 40)] },
            { "cut inside a segment", valid[..(sos - 3)] },
            { "a SOF too short", [0xFF, 0xD8, .. TestJpeg.Segment(0xC0, [8, 0, 8, 0]), 0xFF, 0xD9] },
            { "a width of zero", Patch(TestJpeg.Synthetic([0x00]), 9, 0) }, // SOI, FF C0, length, precision, height, width
            { "ends with FF in the entropy-coded data", [.. TestJpeg.Synthetic([0x00])[..^2], 0xFF] },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Refuses_a_malformed_jpeg_with_a_clear_error(string what, byte[] jpeg)
    {
        var error = Assert.Throws<InvalidDataException>(() => JpegSegments.Strip(jpeg));
        Assert.False(string.IsNullOrWhiteSpace(error.Message), what);
    }

    public static TheoryData<string, byte[]> Unsupported() => new()
    {
        { "arithmetic coding", TestJpeg.Synthetic([0x00], sof: 0xC9) },
        { "lossless", TestJpeg.Synthetic([0x00], sof: 0xC3) },
        { "hierarchical", TestJpeg.Synthetic([0x00], sof: 0xC5) },
        { "12-bit samples", TestJpeg.Synthetic([0x00], sof: 0xC1, precision: 12) },
        { "two components", TestJpeg.Synthetic([0x00], components: 2) },
        { "height given later by DNL", TestJpeg.Synthetic([0x00], height: 0) },
        { "arithmetic conditioning (DAC)", TestJpeg.InsertAfterSoi(TestJpeg.Synthetic([0x00]), TestJpeg.Segment(0xCC, [0x00, 0x10])) },
        { "hierarchical (DHP)", TestJpeg.InsertAfterSoi(TestJpeg.Synthetic([0x00]), TestJpeg.Segment(0xDE, [8, 0, 8, 0, 16, 1, 1, 0x11, 0])) },
        { "hierarchical (EXP)", TestJpeg.InsertAfterSoi(TestJpeg.Synthetic([0x00]), TestJpeg.Segment(0xDF, [0x11])) },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void Refuses_a_jpeg_that_pdf_viewers_cannot_show(string what, byte[] jpeg)
    {
        var error = Assert.Throws<NotSupportedException>(() => JpegSegments.Strip(jpeg));
        Assert.False(string.IsNullOrWhiteSpace(error.Message), what);
    }

    [Fact]
    public void Accepts_baseline_extended_and_progressive_huffman_coding()
    {
        foreach (byte sof in new byte[] { 0xC0, 0xC1, 0xC2 })
            Assert.Equal(16, JpegSegments.Strip(TestJpeg.Synthetic([0x00], sof)).Width);
    }

    [Fact]
    public void Refuses_a_jpeg_larger_than_the_cap()
    {
        Assert.Equal(256 * 1024 * 1024, JpegSegments.MaxBytes);
        var jpeg = TestJpeg.Make();

        var error = Assert.Throws<InvalidDataException>(() => JpegSegments.Strip(jpeg, maxBytes: jpeg.Length - 1));

        Assert.Contains("too large", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(jpeg, JpegSegments.Strip(jpeg, maxBytes: jpeg.Length).Bytes);
    }

    [Fact]
    public void Every_truncation_and_many_corruptions_fail_cleanly_and_quickly()
    {
        var jpeg = JpegWithEverything(6);
        var clock = Stopwatch.StartNew();

        for (var length = 0; length < jpeg.Length; length++)
            StripOrRefuse(jpeg.AsSpan(0, length));

        var random = new Random(20261020);
        for (var i = 0; i < 3_000; i++)
        {
            var damaged = (byte[])jpeg.Clone();
            for (var flips = random.Next(1, 4); flips > 0; flips--)
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            StripOrRefuse(damaged);
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    private static void StripOrRefuse(ReadOnlySpan<byte> jpeg)
    {
        try
        {
            var image = JpegSegments.Strip(jpeg);
            Assert.True(image.Bytes.Length <= jpeg.Length);
            Assert.InRange(image.Orientation, 1, 8);
            Assert.True(image.Width > 0 && image.Height > 0);
        }
        catch (InvalidDataException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static byte[] Patch(byte[] bytes, int at, byte value, int count = 2)
    {
        var copy = (byte[])bytes.Clone();
        copy.AsSpan(at, count).Fill(value);
        return copy;
    }

    /// <summary>
    /// Offsets of every FF <paramref name="marker"/>. Exact for markers other than RSTn: in entropy-coded data FF is
    /// always followed by 00, FF or RSTn.
    /// </summary>
    private static List<int> Offsets(byte[] jpeg, byte marker)
    {
        var found = new List<int>();
        for (var i = 0; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == marker)
                found.Add(i);
        }

        return found;
    }

    static JpegSegmentsTests() => MagickSetup.EnsureInitialized();
}
