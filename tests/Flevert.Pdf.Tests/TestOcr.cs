using ImageMagick;

namespace Flevert.Pdf.Tests;

/// <summary>
/// Scanned PDFs for the OCR tests (docs/specs/ocr.md): each page is a picture of a page of real text, drawn by pdfium at
/// 300 DPI from a <see cref="TestTextPdf"/> and put back into a PDF by Combine into PDF, so the PDF has no text of its own.
/// </summary>
internal static class TestOcr
{
    public const double Dpi = 300;

    /// <summary>Writes one scanned A4 page per entry of <paramref name="pages"/>, its lines split at <c>\n</c>.</summary>
    public static Task WriteScan(string path, params string[] pages) => WriteScan(path, 0, pages);

    /// <summary>
    /// As <see cref="WriteScan(string, string[])"/>, with each picture turned <paramref name="turnedLeft"/> degrees
    /// counterclockwise, as a page scanned sideways is: turning the page as many degrees clockwise shows it upright.
    /// </summary>
    public static async Task WriteScan(string path, int turnedLeft, params string[] pages)
    {
        var work = Path.Combine(Path.GetDirectoryName(path)!, "scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var pictures = new List<string>();
            for (var i = 0; i < pages.Length; i++)
            {
                var text = Path.Combine(work, $"text-{i + 1:D3}.pdf");
                TestTextPdf.Write(text, pages[i]);
                using var pdf = PdfDocument.Open(text);
                var bitmap = pdf.Render(0, Dpi);
                using var image = new MagickImage();
                image.ReadPixels(bitmap.Bgra.Span,
                    new PixelReadSettings((uint)bitmap.Width, (uint)bitmap.Height, StorageType.Char, PixelMapping.BGRA));
                image.Alpha(AlphaAction.Off);
                if (turnedLeft != 0)
                    image.Rotate(360 - turnedLeft);
                image.Density = new Density(Dpi, Dpi, DensityUnit.PixelsPerInch);
                var picture = Path.Combine(work, $"page-{i + 1:D3}.png");
                await image.WriteAsync(picture, MagickFormat.Png);
                pictures.Add(picture);
            }

            var combined = await new ImagesToPdf().RunAsync(pictures, null, CancellationToken.None);
            File.Move(combined, path);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>1 when the texts match once runs of white space are one space, down to 0: one minus the edit distance per character.</summary>
    public static double Similarity(string expected, string actual)
    {
        var e = Normalize(expected);
        var a = Normalize(actual);
        if (e == a)
            return 1;
        if (e.Length == 0 || a.Length == 0)
            return 0;
        return 1 - ((double)Levenshtein(e, a) / Math.Max(e.Length, a.Length));
    }

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int Levenshtein(string s, string t)
    {
        var previous = new int[t.Length + 1];
        var current = new int[t.Length + 1];
        for (var j = 0; j <= t.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= s.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= t.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return previous[t.Length];
    }
}
