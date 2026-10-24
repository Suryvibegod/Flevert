using System.Runtime.InteropServices;

namespace Flevert.Pdf.Tests;

/// <summary>
/// Where pdfium finds a page's characters (FPDFText_GetCharBox) on the page as it is shown, its /Rotate applied, in the
/// pixels of <see cref="PdfDocument.Render"/>: the OCR tests check that the invisible text lies over the words it reads.
/// </summary>
internal static unsafe partial class TestTextBoxes
{
    private const string Library = "pdfium";

    /// <summary>A character's box in pixels, its edges included, from the top left of the rendered page.</summary>
    public readonly record struct Box(int Left, int Top, int Right, int Bottom)
    {
        public Box Union(Box other) =>
            new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    /// <summary>The characters of page <paramref name="pageIndex"/> other than white space, line by line as pdfium reads them.</summary>
    public static List<List<(char Char, Box Box)>> Lines(PdfDocument pdf, int pageIndex, PdfRenderSize size)
    {
        var lines = new List<List<(char, Box)>> { new() };
        lock (Pdfium.Gate)
        {
            using var page = PageHandle.Load(pdf.Handle, pageIndex);
            var native = page.DangerousGetHandle();
            var text = PdfiumNative.FPDFText_LoadPage(native);
            Assert.NotEqual(0, text);
            try
            {
                var count = PdfiumNative.FPDFText_CountChars(text);
                for (var i = 0; i < count; i++)
                {
                    var c = (char)FPDFText_GetUnicode(text, i);
                    if (c == '\n')
                    {
                        lines.Add([]);
                        continue;
                    }

                    if (char.IsWhiteSpace(c))
                        continue;
                    double left, right, bottom, top;
                    Assert.NotEqual(0, FPDFText_GetCharBox(text, i, &left, &right, &bottom, &top));
                    var (x1, y1) = ToDevice(native, size, left, top);
                    var (x2, y2) = ToDevice(native, size, right, bottom);
                    lines[^1].Add((c, new Box(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2))));
                }
            }
            finally
            {
                PdfiumNative.FPDFText_ClosePage(text);
            }
        }

        return lines.Where(line => line.Count > 0).ToList();
    }

    /// <summary>
    /// Checks that the text of page <paramref name="pageIndex"/>, shown upright, lies over the dark pixels of its words:
    /// most characters cover ink, and each line of text starts and ends where the ink of its line does.
    /// </summary>
    public static void AssertTextOverInk(PdfDocument pdf, int pageIndex)
    {
        const double dpi = 100;
        var bitmap = pdf.Render(pageIndex, dpi);
        var size = pdf.GetRenderSize(pageIndex, dpi);
        var pixels = bitmap.Bgra.Span;
        var ink = new bool[bitmap.Width * bitmap.Height];
        for (var at = 0; at < ink.Length; at++)
            ink[at] = pixels[at * 4] + pixels[(at * 4) + 1] + pixels[(at * 4) + 2] < 3 * 128;

        var lines = Lines(pdf, pageIndex, size);
        var chars = lines.SelectMany(line => line).ToList();
        Assert.NotEmpty(chars);
        var onInk = chars.Count(c => AnyInk(ink, bitmap.Width, bitmap.Height, c.Box));
        Assert.True(onInk >= 0.9 * chars.Count, $"{onInk} of {chars.Count} characters lie over ink.");

        // A line of 14-point text at 100 DPI is about 20 pixels tall, its letters about 8 wide.
        const int tolerance = 10;
        foreach (var line in lines.Where(line => line.Count >= 3))
        {
            var box = line.Skip(1).Aggregate(line[0].Box, (union, c) => union.Union(c.Box));
            var said = new string([.. line.Select(c => c.Char)]);
            // The middle of the line only, so that the ink of the lines above and below is left out.
            var middle = (box.Top + box.Bottom) / 2;
            var (inkLeft, inkRight) = InkColumns(ink, bitmap.Width, Math.Max(0, middle - 2), Math.Min(bitmap.Height - 1, middle + 2));
            Assert.True(inkLeft >= 0, $"No ink across the middle of '{said}' at {box}.");
            Assert.True(Math.Abs(box.Left - inkLeft) <= tolerance && Math.Abs(box.Right - inkRight) <= tolerance,
                $"'{said}' spans {box.Left}-{box.Right}, its ink {inkLeft}-{inkRight}.");
        }
    }

    private static bool AnyInk(bool[] ink, int width, int height, Box box)
    {
        for (var y = Math.Max(0, box.Top); y <= Math.Min(height - 1, box.Bottom); y++)
        {
            for (var x = Math.Max(0, box.Left); x <= Math.Min(width - 1, box.Right); x++)
            {
                if (ink[(y * width) + x])
                    return true;
            }
        }

        return false;
    }

    private static (int Left, int Right) InkColumns(bool[] ink, int width, int top, int bottom)
    {
        var (left, right) = (-1, -1);
        for (var y = top; y <= bottom; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!ink[(y * width) + x])
                    continue;
                if (left < 0 || x < left)
                    left = x;
                right = Math.Max(right, x);
            }
        }

        return (left, right);
    }

    private static (int X, int Y) ToDevice(nint page, PdfRenderSize size, double x, double y)
    {
        int deviceX, deviceY;
        Assert.NotEqual(0, FPDF_PageToDevice(page, 0, 0, size.Width, size.Height, 0, x, y, &deviceX, &deviceY));
        return (deviceX, deviceY);
    }

    [LibraryImport(Library)]
    private static partial uint FPDFText_GetUnicode(nint textPage, int index);

    [LibraryImport(Library)]
    private static partial int FPDFText_GetCharBox(nint textPage, int index, double* left, double* right, double* bottom, double* top);

    [LibraryImport(Library)]
    private static partial int FPDF_PageToDevice(
        nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);
}
