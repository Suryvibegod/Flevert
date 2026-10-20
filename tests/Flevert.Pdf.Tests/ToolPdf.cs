using System.Globalization;
using System.Text;

namespace Flevert.Pdf.Tests;

/// <summary>
/// PDFs for the v0.2.2 tools (docs/specs/pdf.md, "Testing Strategy (v0.2.2)"), written at run time with the .NET base
/// library like <see cref="TestPdf"/> and <see cref="MaliciousPdf"/>: nothing downloaded or committed.
/// </summary>
internal static class ToolPdf
{
    /// <summary>
    /// One page per entry of <paramref name="widths"/> (points wide, 400 high), each drawing an image of
    /// <paramref name="imageBytes"/> bytes of noise, stored as it is: pages that weigh what their image weighs, and whose
    /// width tells which page they were.
    /// </summary>
    public static void WriteHeavyPages(string path, int imageBytes, params int[] widths)
    {
        var random = new Random(22);
        var side = (int)Math.Ceiling(Math.Sqrt(imageBytes / 3.0));
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Count " + widths.Length + " /Kids ["
                + string.Join(" ", Enumerable.Range(0, widths.Length).Select(i => (3 + (3 * i)) + " 0 R")) + "] >>"),
        };
        for (var i = 0; i < widths.Length; i++)
        {
            var page = 3 + (3 * i);
            objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widths[i]} 400] /Resources << /XObject << /Im0 {page + 1} 0 R >> >> /Contents {page + 2} 0 R >>")));
            var pixels = new byte[side * side * 3];
            random.NextBytes(pixels);
            objects.Add([
                .. Ascii(string.Create(CultureInfo.InvariantCulture,
                    $"<< /Type /XObject /Subtype /Image /Width {side} /Height {side} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} >>\nstream\n")),
                .. pixels,
                .. "\nendstream"u8,
            ]);
            var content = Ascii(string.Create(CultureInfo.InvariantCulture, $"q {widths[i]} 0 0 400 0 0 cm /Im0 Do Q"));
            objects.Add([.. Ascii($"<< /Length {content.Length} >>\nstream\n"), .. content, .. "\nendstream"u8]);
        }

        Write(path, objects);
    }

    /// <summary>Writes objects 1..n, a cross-reference table and a trailer whose root is object 1.</summary>
    public static void Write(string path, IReadOnlyList<byte[]> objects, string trailer = "")
    {
        using var file = File.Create(path);
        var offsets = new List<long>();
        void Put(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        Put("%PDF-1.7\n");
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Put(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n"));
            file.Write(objects[i]);
            Put("\nendobj\n");
        }

        var xref = file.Position;
        Put(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
            Put(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n"));
        Put(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{trailer} >>\nstartxref\n{xref}\n%%EOF\n"));
    }

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>A stream object: its dictionary entries, then <paramref name="data"/>.</summary>
    public static byte[] Stream(string entries, byte[] data) =>
        [.. Ascii(string.Create(CultureInfo.InvariantCulture, $"<< {entries} /Length {data.Length} >>\nstream\n")), .. data, .. "\nendstream"u8];
}
