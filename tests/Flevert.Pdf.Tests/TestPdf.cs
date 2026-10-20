using System.Globalization;
using System.Text;
using System.Text.Json;
using Flevert.Core;

namespace Flevert.Pdf.Tests;

internal static class TestPdf
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>
    /// A minimal valid PDF of <paramref name="pages"/> empty Letter pages (fixtures/README.md). Without
    /// <paramref name="resources"/>, the pages lack the /Resources entry PDF requires: viewers open it, and qpdf repairs
    /// it with a warning.
    /// </summary>
    public static void WriteBlank(string path, int pages, bool resources = false)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /MediaBox [0 0 612 792] /Count " + pages + " /Kids ["
                + string.Join(" ", Enumerable.Range(3, pages).Select(n => n + " 0 R")) + "] >>",
        };
        objects.AddRange(Enumerable.Repeat(resources ? "<< /Type /Page /Parent 2 0 R /Resources << >> >>" : "<< /Type /Page /Parent 2 0 R >>", pages));
        WriteObjects(path, objects);
    }

    /// <summary>
    /// A PDF of blank pages of the given widths (in points, 100 high each): a page's width tells which file and which page
    /// it came from, so a test can check the order of pages after a merge or a split.
    /// </summary>
    public static void WriteWidths(string path, params int[] widths)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count " + widths.Length + " /Kids ["
                + string.Join(" ", Enumerable.Range(3, widths.Length).Select(n => n + " 0 R")) + "] >>",
        };
        objects.AddRange(widths.Select(width =>
            string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} 100] /Resources << >> >>")));
        WriteObjects(path, objects);
    }

    /// <summary>The width in points of every page of the PDF at <paramref name="path"/>, in page order (pdfium).</summary>
    public static int[] PageWidths(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return Enumerable.Range(0, pdf.PageCount).Select(page => (int)Math.Round(pdf.GetPageSize(page).Width)).ToArray();
    }

    // Objects 1..n in order, an xref table and a trailer whose root is object 1.
    private static void WriteObjects(string path, List<string> objects)
    {
        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(body.Length);
            body.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = body.Length;
        body.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            body.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        body.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(body.ToString()));
    }

    /// <summary>
    /// One blank page, and an object no page uses: an array of <paramref name="numbers"/> zeros the catalog points at.
    /// Two million take qpdf 12.4.2 about 260 MB to parse, from a 4 MB file.
    /// </summary>
    public static void WriteWithLargeArray(string path, int numbers)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Extra 4 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
            "[" + new StringBuilder(numbers * 2).Insert(0, "0 ", numbers) + "]",
        ];
        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(body.Length);
            body.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = body.Length;
        body.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            body.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        body.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(body.ToString()));
    }

    /// <summary>
    /// One page whose content is a single stream of <paramref name="bytes"/> spaces, marked as a JPEG (DCTDecode), as in
    /// a scan. qpdf 12.4.2 copies such a stream without decoding it and needs about twice its size (300 MB took 602 MB).
    /// </summary>
    public static void WriteWithOneStream(string path, long bytes)
    {
        using var file = File.Create(path);
        var offsets = new List<long>();
        void Write(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n");
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 4 0 R >>",
        ];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(file.Position);
            Write(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n"));
        }

        offsets.Add(file.Position);
        Write(string.Create(CultureInfo.InvariantCulture, $"4 0 obj\n<< /Filter /DCTDecode /Length {bytes} >>\nstream\n"));
        var chunk = new byte[1 << 20];
        Array.Fill(chunk, (byte)' ');
        for (var left = bytes; left > 0; left -= chunk.Length)
            file.Write(chunk, 0, (int)Math.Min(left, chunk.Length));
        Write("\nendstream\nendobj\n");
        var xref = file.Position;
        Write("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n"));
        Write(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
    }

    /// <summary>
    /// One A4 page whose only content is a free-form triangle mesh (shading type 4) of <paramref name="triangles"/>
    /// triangles that each cover the whole page: a page made to stall a PDF reader. pdfium draws every triangle in one
    /// call, about 2.5 ms each at 150 DPI on the dev machine (2 000 took 5 s, 20 000 took 50 s), so a million keep it busy
    /// for about 40 minutes at 77 MB, from a 60 KB file (Flate packs the repeated triangle).
    /// </summary>
    public static void WriteStallingPage(string path, int triangles = 1_000_000)
    {
        // Per vertex: flag (8 bits), x and y (16 bits each), red, green, blue (8 bits each).
        byte[] triangle =
        [
            0, 0x00, 0x00, 0x00, 0x00, 255, 0, 0,
            0, 0xFF, 0xFF, 0x00, 0x00, 255, 0, 0,
            0, 0x00, 0x00, 0xFF, 0xFF, 255, 0, 0,
        ];
        var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (var i = 0; i < triangles; i++)
                zlib.Write(triangle);
        }

        var mesh = packed.ToArray();
        WriteObjects(path,
        [
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Shading << /Sh0 4 0 R >> >> /Contents 5 0 R >>"),
            [
                .. Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                    $"<< /ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 16 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 1200 0 1700 0 1 0 1 0 1] /Filter /FlateDecode /Length {mesh.Length} >>\nstream\n")),
                .. mesh,
                .. "\nendstream"u8,
            ],
            Encoding.ASCII.GetBytes("<< /Length 7 >>\nstream\n/Sh0 sh\nendstream"),
        ]);
    }

    /// <summary>
    /// One square page of <paramref name="points"/> points a side, white: 14 400 (the largest PDF allows) renders at
    /// 10 000 x 10 000 px, 400 MB of pixels.
    /// </summary>
    public static void WriteSquarePage(string path, int points)
    {
        WriteObjects(path,
        [
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {points} {points}] /Resources << >> >>")),
        ]);
    }

    private static void WriteObjects(string path, IReadOnlyList<byte[]> objects)
    {
        using var file = File.Create(path);
        var offsets = new List<long>();
        void Write(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n");
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Write(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n"));
            file.Write(objects[i]);
            Write("\nendobj\n");
        }

        var xref = file.Position;
        Write(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
            Write(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n"));
        Write(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
    }

    /// <summary>The flevert.exe the solution built, which the tests run as the PDF worker and as the app.</summary>
    public static string FlevertExe
    {
        get
        {
            // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = output.Parent!.Name;
            var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            var exe = Path.Combine(root, "src", "Flevert.Cli", "bin", configuration, output.Name, "flevert.exe");
            Assert.True(File.Exists(exe), $"Build the solution first; missing {exe}");
            return exe;
        }
    }

    /// <summary>The PDF worker of the built flevert.exe, with the product's limits.</summary>
    public static PdfWorker Worker => new(FlevertExe);

    /// <summary>The pixel at (x, y) as (red, green, blue, alpha).</summary>
    public static (byte R, byte G, byte B, byte A) Pixel(PdfBitmap bitmap, int x, int y)
    {
        var span = bitmap.Bgra.Span;
        var at = (y * bitmap.Stride) + (x * 4);
        return (span[at + 2], span[at + 1], span[at], span[at + 3]);
    }

    /// <summary>
    /// Where the red mark of a page of <c>rotations.pdf</c> shows on screen, after the page's own rotation. Clockwise
    /// order, so turning the page a quarter clockwise moves the mark to the next value.
    /// </summary>
    public enum Corner { TopLeft, TopRight, BottomRight, BottomLeft }

    /// <summary>The corner whose sample is red in page <paramref name="pageIndex"/> as rendered by pdfium.</summary>
    public static Corner MarkCorner(PdfDocument pdf, int pageIndex)
    {
        var bitmap = pdf.Render(pageIndex, 36); // a 400 x 300 pt page is 200 x 150 px, its mark 50 px wide
        var (right, bottom) = (bitmap.Width - 6, bitmap.Height - 6);
        var red = new[] { (5, 5), (right, 5), (right, bottom), (5, bottom) }
            .Select((point, corner) => (Corner: (Corner)corner, Pixel: Pixel(bitmap, point.Item1, point.Item2)))
            .Where(sample => sample.Pixel == (255, 0, 0, 255))
            .ToList();
        Assert.True(red.Count == 1, $"page {pageIndex + 1}: expected the mark in exactly one corner, found {red.Count}");
        return red[0].Corner;
    }

    /// <summary>Each page object's own /Rotate, in page order, as qpdf reads it (0 when the page has none).</summary>
    public static async Task<int[]> ReadRotations(string path)
    {
        var qpdf = Path.Combine(AppContext.BaseDirectory, "qpdf.exe");
        var run = await ExternalTool.RunAsync(qpdf, ["--json=2", "--json-key=pages", "--json-key=qpdf", path], TimeSpan.FromSeconds(30));
        Assert.True(run.ExitCode == 0, run.StandardError);
        using var json = JsonDocument.Parse(run.StandardOutput);
        var objects = json.RootElement.GetProperty("qpdf")[1];
        return json.RootElement.GetProperty("pages").EnumerateArray()
            .Select(page => objects.GetProperty("obj:" + page.GetProperty("object").GetString()).GetProperty("value"))
            .Select(page => page.TryGetProperty("/Rotate", out var rotate) ? rotate.GetInt32() : 0)
            .ToArray();
    }

    /// <summary>The files in <paramref name="folder"/>, by name, sorted.</summary>
    public static string[] FileNames(string folder) =>
        Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// <paramref name="path"/> with 8.3 names expanded, as the runner names a file (on CI, %TEMP% is
    /// <c>C:\Users\RUNNER~1\…</c>), so a test can build a path of an exact length.
    /// </summary>
    public static string LongPath(string path)
    {
        var buffer = new char[short.MaxValue];
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? throw new System.ComponentModel.Win32Exception() : new string(buffer, 0, (int)length);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, char[] longPath, uint length);
}
