using System.Globalization;
using System.Text;

namespace Flevert.Pdf.Tests;

internal static class TestPdf
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>A minimal valid PDF of <paramref name="pages"/> empty Letter pages (fixtures/README.md).</summary>
    public static void WriteBlank(string path, int pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /MediaBox [0 0 612 792] /Count " + pages + " /Kids ["
                + string.Join(" ", Enumerable.Range(3, pages).Select(n => n + " 0 R")) + "] >>",
        };
        objects.AddRange(Enumerable.Repeat("<< /Type /Page /Parent 2 0 R >>", pages));

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

    /// <summary>The pixel at (x, y) as (red, green, blue, alpha).</summary>
    public static (byte R, byte G, byte B, byte A) Pixel(PdfBitmap bitmap, int x, int y)
    {
        var span = bitmap.Bgra.Span;
        var at = (y * bitmap.Stride) + (x * 4);
        return (span[at + 2], span[at + 1], span[at], span[at + 3]);
    }
}
