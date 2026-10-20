using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Flevert.Pdf.Tests;

/// <summary>
/// Malicious and broken PDFs for the hardening tests (T18, fixtures/README.md), written at run time with the .NET base
/// library only: nothing here is downloaded, and none of it is real malware. Each is the smallest file that does what an
/// attacker's would: inflate to gigabytes, loop, nest, lie about its pages, or ask a reader to run something.
/// </summary>
internal static class MaliciousPdf
{
    /// <summary>Every fixture by name, with what it is.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bomb-content"] = "the page's content stream is Flate that inflates to 4 GiB (4 MB of file)",
        ["bomb-double-flate"] = "a stream nothing on the page uses, Flate inside Flate, inflating to 4 GiB (10 KB of file)",
        ["bomb-double-flate-content"] = "the page's content stream, Flate inside Flate, inflating to 4 GiB (10 KB of file)",
        ["bomb-image"] = "a 1 000 x 1 000 grey image whose Flate data inflates to 4 GiB",
        ["bomb-object-stream"] = "the page tree inside an object stream that inflates to 4 GiB, so a reader must inflate it to parse the file",
        ["cycle-kids-self"] = "the page tree's /Kids holds the page tree itself",
        ["cycle-kids-loop"] = "a /Pages node below the root lists the root among its /Kids",
        ["cycle-parent"] = "the page's /Parent chain loops between two /Pages nodes",
        ["cycle-self-reference"] = "objects that are a reference to themselves (4 0 obj 4 0 R), used as /Resources and /MediaBox",
        ["pages-100000"] = "100 000 empty pages",
        ["pages-2001"] = "2 001 empty pages, one past the limit",
        ["count-lie-low"] = "100 000 empty pages under a /Count of 1",
        ["count-lie-high"] = "one page under a /Count of 2 000 000 000",
        ["page-giant"] = "a page 2 000 000 000 points square",
        ["page-zero"] = "a page with a MediaBox of [0 0 0 0]",
        ["page-inverted"] = "an inverted MediaBox of 100 million points with a /UserUnit of 75 000",
        ["xref-wrong-offsets"] = "a 3-page PDF whose cross-reference table points at the wrong bytes",
        ["xref-missing"] = "a 3-page PDF without cross-reference table or trailer",
        ["truncated"] = "the first half of a 3-page PDF",
        ["startxref-past-end"] = "a 3-page PDF whose startxref points far past the end of the file",
        ["not-pdf-png"] = "a 1 x 1 PNG named .pdf",
        ["not-pdf-zip"] = "a zip archive named .pdf",
        ["not-pdf-empty"] = "an empty file named .pdf",
        ["header-only"] = "only the line %PDF-1.7",
        ["nested-arrays"] = "an array nested 100 000 deep",
        ["nested-dictionaries"] = "a dictionary nested 100 000 deep",
        ["deep-page-tree"] = "one page below a chain of 20 000 /Pages nodes",
        ["many-objects"] = "one page, and an array of references to 300 000 small objects (16 MB)",
        ["launch-action"] = "an /OpenAction and page action that launch cmd.exe to write a marker file, and document JavaScript",
        ["encrypt-unknown-handler"] = "encrypted with a security handler (/Filter) nobody implements",
        ["signed"] = "one page with a signed signature field (/FT /Sig with a /V signature dictionary; not a valid signature)",
    };

    /// <summary>
    /// The file the <c>launch-action</c> fixture at <paramref name="pdfPath"/> asks cmd.exe to write: in %TEMP%, named
    /// after a hash of the PDF's path, so the PDF holds it in ASCII whatever the test folder is called.
    /// </summary>
    public static string LaunchMarker(string pdfPath) => Path.Combine(Path.GetTempPath(), LaunchMarkerName(pdfPath));

    private static string LaunchMarkerName(string pdfPath) =>
        "flevert-launched-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pdfPath)))[..16] + ".txt";

    /// <summary>The program and parameters of the launch action in <c>launch-action</c>, as a viewer would run them.</summary>
    public static (string File, string Parameters) LaunchCommand(string pdfPath) =>
        ("cmd.exe", $"/c echo launched > \"%TEMP%\\{LaunchMarkerName(pdfPath)}\"");

    private const int GiB = 1024; // in MiB
    private static readonly byte[] Catalog = Ascii("<< /Type /Catalog /Pages 2 0 R >>");
    private static readonly byte[] CatalogWithExtra = Ascii("<< /Type /Catalog /Pages 2 0 R /Extra 4 0 R >>");
    private static readonly byte[] OnePageTree = Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
    private static readonly byte[] BlankPage = Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>");
    private static readonly byte[] PageWithContents = Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 4 0 R >>");

    /// <summary>Writes the fixture <paramref name="name"/> at <paramref name="path"/> and returns the path.</summary>
    public static string Write(string name, string path)
    {
        var bytes = name switch
        {
            "bomb-content" => Document(Catalog, OnePageTree, PageWithContents, Stream("/Filter /FlateDecode", ZerosDeflated([], 4 * GiB))),
            "bomb-double-flate" => Document(CatalogWithExtra, OnePageTree, BlankPage, Stream("/Filter [/FlateDecode /FlateDecode]", DoubleBomb.Value)),
            "bomb-double-flate-content" => Document(Catalog, OnePageTree, PageWithContents, Stream("/Filter [/FlateDecode /FlateDecode]", DoubleBomb.Value)),
            "bomb-image" => Document(Catalog, OnePageTree,
                Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>"),
                Stream("/Type /XObject /Subtype /Image /Width 1000 /Height 1000 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
                    ZerosDeflated([], 4 * GiB)),
                Stream("", Ascii("q 612 0 0 792 0 0 cm /Im0 Do Q"))),
            "bomb-object-stream" => ObjectStreamBomb(),
            "cycle-kids-self" => Document(Catalog, Ascii("<< /Type /Pages /Count 1 /Kids [2 0 R] >>")),
            "cycle-kids-loop" => Document(Catalog, Ascii("<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>"), BlankPage,
                Ascii("<< /Type /Pages /Parent 2 0 R /Count 1 /Kids [2 0 R] >>")),
            "cycle-parent" => Document(Catalog, OnePageTree,
                Ascii("<< /Type /Page /Parent 4 0 R /MediaBox [0 0 612 792] /Resources << >> >>"),
                Ascii("<< /Type /Pages /Parent 5 0 R /Kids [3 0 R] /Count 1 >>"),
                Ascii("<< /Type /Pages /Parent 4 0 R /Kids [4 0 R] /Count 1 >>")),
            "cycle-self-reference" => Document(CatalogWithExtra, OnePageTree,
                Ascii("<< /Type /Page /Parent 2 0 R /MediaBox 5 0 R /Resources 4 0 R >>"), Ascii("4 0 R"), Ascii("5 0 R")),
            "pages-100000" => BlankPages(100_000, 100_000),
            "pages-2001" => BlankPages(PdfDocument.MaxPages + 1, PdfDocument.MaxPages + 1),
            "count-lie-low" => BlankPages(100_000, 1),
            "count-lie-high" => BlankPages(1, 2_000_000_000),
            "page-giant" => Document(Catalog, OnePageTree,
                Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2000000000 2000000000] /Resources << >> >>")),
            "page-zero" => Document(Catalog, OnePageTree, Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 0 0] /Resources << >> >>")),
            "page-inverted" => Document(Catalog, OnePageTree,
                Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [612 792 -99999999 -99999999] /UserUnit 75000 /Resources << >> >>")),
            "xref-wrong-offsets" => ReplaceOnce(BlankPages(3, 3), "0000000009 00000 n", "0000099999 00000 n"),
            "xref-missing" => [.. BlankPages(3, 3).AsSpan(0, IndexOf(BlankPages(3, 3), "xref\n")), .. Ascii("%%EOF\n")],
            "truncated" => BlankPages(3, 3)[..(BlankPages(3, 3).Length / 2)],
            "startxref-past-end" => StartXrefPastEnd(),
            "not-pdf-png" => Convert.FromHexString(
                "89504E470D0A1A0A0000000D49484452000000010000000108060000001F15C4890000000D4944415478DA63F8FFFF3F0005FE02FEA7D6A4D60000000049454E44AE426082"),
            "not-pdf-zip" => Zip(),
            "not-pdf-empty" => [],
            "header-only" => Ascii("%PDF-1.7\n"),
            "nested-arrays" => Document(CatalogWithExtra, OnePageTree, BlankPage, Ascii(new string('[', 100_000) + new string(']', 100_000))),
            "nested-dictionaries" => Document(CatalogWithExtra, OnePageTree, BlankPage,
                Ascii(string.Concat(Enumerable.Repeat("<< /A ", 100_000)) + "1" + string.Concat(Enumerable.Repeat(" >>", 100_000)))),
            "deep-page-tree" => DeepPageTree(20_000),
            "many-objects" => ManyObjects(300_000),
            "launch-action" => LaunchAction(path),
            "encrypt-unknown-handler" => Document(
                [Catalog, OnePageTree, BlankPage, Ascii("<< /Filter /Flevert.Unknown /V 4 /R 4 /Length 128 /O <00> /U <00> /P -4 >>")],
                " /Encrypt 4 0 R /ID [<0123456789abcdef0123456789abcdef> <0123456789abcdef0123456789abcdef>]"),
            "signed" => Signed(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such fixture."),
        };
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // Flate of Flate: the inner 4 GiB bomb (4 MB) packs again to about 10 KB.
    private static readonly Lazy<byte[]> DoubleBomb = new(() =>
    {
        var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(ZerosDeflated([], 4 * GiB));
        return packed.ToArray();
    });

    /// <summary>
    /// A zlib stream that inflates to <paramref name="prefix"/> followed by <paramref name="zeroMiB"/> MiB of zero bytes,
    /// without compressing gigabytes: once the compressor's window is all zeros, each further MiB of zeros compresses to the
    /// same sync-flushed block, so that block is repeated. A final empty block and the Adler-32 of the whole end it.
    /// </summary>
    public static byte[] ZerosDeflated(byte[] prefix, int zeroMiB)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(zeroMiB, 3);
        var mib = new byte[1 << 20];
        var packed = new MemoryStream();
        byte[] Next(ZLibStream zlib, ReadOnlySpan<byte> data)
        {
            var start = packed.Length;
            zlib.Write(data);
            zlib.Flush(); // a sync flush: the block ends on a byte boundary
            return packed.ToArray()[(int)start..];
        }

        var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true);
        var head = Next(zlib, [.. prefix, .. mib]);
        var repeat = Next(zlib, mib);
        if (!repeat.AsSpan().SequenceEqual(Next(zlib, mib)))
            throw new InvalidOperationException("The compressor does not repeat its block for zeros; the bomb cannot be built this way.");
        // Not disposed: disposing would write the compressor's own end after the blocks taken above.

        var result = new MemoryStream();
        result.Write(head);
        for (var i = 1; i < zeroMiB; i++)
            result.Write(repeat);
        result.Write([0x03, 0x00]); // a final, empty block with fixed codes
        uint a = 1, b = 0;
        foreach (var x in prefix)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        b = (uint)((b + ((ulong)a * ((ulong)zeroMiB << 20))) % 65521);
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, (b << 16) | a);
        result.Write(adler);
        return result.ToArray();
    }

    // Objects 2 and 3 (the page tree and the page) live in object stream 4, whose data goes on with 4 GiB of zeros; a
    // cross-reference stream (5) says so, since a classic table cannot point into an object stream.
    private static byte[] ObjectStreamBomb()
    {
        var pages = Encoding.ASCII.GetString(OnePageTree).PadRight(60);
        var header = $"2 0 3 {pages.Length} ";
        var data = ZerosDeflated(Ascii(header + pages + Encoding.ASCII.GetString(BlankPage)), 4 * GiB);
        var body = new MemoryStream();
        void Add(string text) => body.Write(Ascii(text));
        Add("%PDF-1.7\n");
        var catalog = body.Position;
        Add("1 0 obj\n");
        body.Write(Catalog);
        Add("\nendobj\n");
        var objects = body.Position;
        Add("4 0 obj\n");
        body.Write(Stream($"/Type /ObjStm /N 2 /First {header.Length} /Filter /FlateDecode", data));
        Add("\nendobj\n");
        var xref = body.Position;
        (byte Type, long Field2, int Field3)[] rows = [(0, 0, 65535), (1, catalog, 0), (2, 4, 0), (2, 4, 1), (1, objects, 0), (1, xref, 0)];
        var table = new byte[rows.Length * 7];
        for (var i = 0; i < rows.Length; i++)
        {
            table[i * 7] = rows[i].Type;
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(i * 7 + 1), (uint)rows[i].Field2);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(i * 7 + 5), (ushort)rows[i].Field3);
        }

        Add("5 0 obj\n");
        body.Write(Stream("/Type /XRef /Size 6 /W [1 4 2] /Root 1 0 R", table));
        Add(string.Create(CultureInfo.InvariantCulture, $"\nendobj\nstartxref\n{xref}\n%%EOF\n"));
        return body.ToArray();
    }

    private static byte[] BlankPages(int pages, int count)
    {
        var kids = string.Join(' ', Enumerable.Range(3, pages).Select(n => n.ToString(CultureInfo.InvariantCulture) + " 0 R"));
        return Document([Catalog, Ascii($"<< /Type /Pages /Count {count.ToString(CultureInfo.InvariantCulture)} /Kids [{kids}] >>"),
            .. Enumerable.Repeat(BlankPage, pages)]);
    }

    private static byte[] StartXrefPastEnd()
    {
        var good = BlankPages(3, 3);
        var at = IndexOf(good, "startxref\n");
        return [.. good.AsSpan(0, at), .. Ascii("startxref\n999999999999\n%%EOF\n")];
    }

    private static byte[] DeepPageTree(int depth)
    {
        var objects = new List<byte[]> { Catalog };
        for (var i = 0; i < depth; i++)
        {
            var self = i + 2;
            var parent = i == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" /Parent {self - 1} 0 R");
            objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Pages{parent} /Count 1 /Kids [{self + 1} 0 R] >>")));
        }

        objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture,
            $"<< /Type /Page /Parent {depth + 1} 0 R /MediaBox [0 0 612 792] /Resources << >> >>")));
        return Document([.. objects]);
    }

    /// <summary>
    /// One page and <paramref name="count"/> small objects an array refers to: few pages, so the page limit lets it
    /// through, but qpdf parses and writes every object. A million (53 MB) take qpdf 12.4.2 about 13 s to rotate on the
    /// dev machine: the slow input of the cancel and kill tests.
    /// </summary>
    public static void WriteManyObjects(string path, int count) => File.WriteAllBytes(path, ManyObjects(count));

    /// <summary>
    /// <paramref name="count"/> small objects, as <see cref="WriteManyObjects"/>, and the double Flate bomb of
    /// <c>bomb-double-flate</c> among them: a PDF that object streams make much smaller, and that takes seconds to decode.
    /// </summary>
    public static void WriteManyObjectsAndBomb(string path, int count)
    {
        var references = new StringBuilder(count * 11).Append('[');
        for (var i = 0; i <= count; i++)
            references.Append(CultureInfo.InvariantCulture, $"{i + 5} 0 R ");
        references.Append(']');
        var zero = Ascii("0");
        File.WriteAllBytes(path, Document([CatalogWithExtra, OnePageTree, BlankPage, Ascii(references.ToString()), .. Enumerable.Repeat(zero, count),
            Stream("/Filter [/FlateDecode /FlateDecode]", DoubleBomb.Value)]));
    }

    private static byte[] ManyObjects(int count)
    {
        var references = new StringBuilder(count * 11).Append('[');
        for (var i = 0; i < count; i++)
            references.Append(CultureInfo.InvariantCulture, $"{i + 5} 0 R ");
        references.Append(']');
        var zero = Ascii("0");
        return Document([CatalogWithExtra, OnePageTree, BlankPage, Ascii(references.ToString()), .. Enumerable.Repeat(zero, count)]);
    }

    private static byte[] LaunchAction(string pdfPath)
    {
        var (file, parameters) = LaunchCommand(pdfPath);
        return Document(
            Ascii("<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R /Names << /JavaScript << /Names [(a) 5 0 R] >> >> /AA << /WC 5 0 R >> >>"),
            OnePageTree,
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /AA << /O 4 0 R >> >>"),
            Encoding.UTF8.GetBytes($"<< /S /Launch /Win << /F ({PdfString(file)}) /P ({PdfString(parameters)}) >> /NewWindow true >>"),
            Ascii("<< /S /JavaScript /JS (app.launchURL\\('https://example.invalid/'\\); this.exportDataObject\\({cName: 'a', nLaunch: 2}\\);) >>"));
    }

    private static string PdfString(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    private static byte[] Signed() => Document(
        Ascii("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> >>"),
        OnePageTree,
        Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Annots [4 0 R] >>"),
        Ascii("<< /FT /Sig /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /F 132 /T (Signature1) /P 3 0 R /V 5 0 R >>"),
        Ascii("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 100 200 300] /Contents <"
            + new string('0', 8192) + "> /M (D:20261020000000Z) >>"));

    private static byte[] Zip()
    {
        var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("a.txt").Open());
            entry.Write("hello");
        }

        return bytes.ToArray();
    }

    private static byte[] Document(params byte[][] objects) => Document(objects, "");

    // Objects 1..n in order, a cross-reference table and a trailer whose root is object 1.
    private static byte[] Document(byte[][] objects, string trailerExtra)
    {
        var body = new MemoryStream();
        void Add(string text) => body.Write(Ascii(text));
        Add("%PDF-1.7\n");
        var offsets = new long[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = body.Position;
            Add(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n"));
            body.Write(objects[i]);
            Add("\nendobj\n");
        }

        var xref = body.Position;
        var table = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R{trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        Add(table.ToString());
        return body.ToArray();
    }

    private static byte[] Stream(string entries, byte[] data) =>
        [.. Ascii(string.Create(CultureInfo.InvariantCulture, $"<< {entries} /Length {data.Length} >>\nstream\n")), .. data, .. Ascii("\nendstream")];

    private static byte[] ReplaceOnce(byte[] bytes, string from, string to)
    {
        var at = IndexOf(bytes, from);
        return [.. bytes.AsSpan(0, at), .. Ascii(to), .. bytes.AsSpan(at + from.Length)];
    }

    private static int IndexOf(byte[] bytes, string text)
    {
        var at = bytes.AsSpan().IndexOf(Ascii(text));
        return at >= 0 ? at : throw new InvalidOperationException($"\"{text}\" is not in the fixture.");
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
