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
