using System.Runtime.InteropServices;

namespace Flevert.Pdf.Tests;

/// <summary>
/// PDFs with real text, written by pdfium at run time with Arial embedded (Windows' C:\Windows\Fonts\arial.ttf, which has
/// the Vietnamese letters): each line of a page is a text object in that font, and pdfium writes the font's ToUnicode
/// map, as a word processor's PDF export does. Nothing is downloaded, and no font is committed.
/// </summary>
internal static unsafe partial class TestTextPdf
{
    private const string Library = "pdfium";
    private const int FontTrueType = 2;
    private const float FontSize = 14;

    private static string Arial => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");

    /// <summary>Writes one A4 page per entry of <paramref name="pages"/>, its lines (split at <c>\n</c>) from the top.</summary>
    public static void Write(string path, params string[] pages)
    {
        var font = File.ReadAllBytes(Arial);
        lock (Pdfium.Gate)
        {
            Pdfium.EnsureInitialized();
            var document = FPDF_CreateNewDocument();
            Assert.NotEqual(0, document);
            try
            {
                for (var index = 0; index < pages.Length; index++)
                    AddPage(document, index, pages[index], font);
                using var file = File.Create(path);
                using var sink = new StreamSink(file);
                Assert.NotEqual(0, PdfiumNative.FPDF_SaveAsCopy(document, sink.Write, PdfiumNative.SaveNoIncremental));
                sink.ThrowIfFailed();
            }
            finally
            {
                PdfiumNative.FPDF_CloseDocument(document);
            }
        }
    }

    private static void AddPage(nint document, int index, string text, byte[] fontData)
    {
        var page = PdfiumNative.FPDFPage_New(document, index, 595, 842);
        Assert.NotEqual(0, page);
        nint font;
        fixed (byte* data = fontData)
            font = FPDFText_LoadFont(document, data, (uint)fontData.Length, FontTrueType, cid: 1);
        Assert.NotEqual(0, font);
        try
        {
            var y = 780.0;
            foreach (var line in text.Split('\n'))
            {
                if (line.Length > 0)
                {
                    var item = FPDFPageObj_CreateTextObj(document, font, FontSize);
                    Assert.NotEqual(0, item);
                    fixed (char* chars = line + "\0")
                        Assert.NotEqual(0, FPDFText_SetText(item, (ushort*)chars));
                    FPDFPageObj_Transform(item, 1, 0, 0, 1, 50, y);
                    Assert.NotEqual(0, PdfiumNative.FPDFPage_InsertObject(page, item));
                }

                y -= 24;
            }

            Assert.NotEqual(0, PdfiumNative.FPDFPage_GenerateContent(page));
        }
        finally
        {
            FPDFFont_Close(font);
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    [LibraryImport(Library)]
    private static partial nint FPDF_CreateNewDocument();

    [LibraryImport(Library)]
    private static partial nint FPDFText_LoadFont(nint document, byte* data, uint size, int fontType, int cid);

    [LibraryImport(Library)]
    private static partial void FPDFFont_Close(nint font);

    [LibraryImport(Library)]
    private static partial nint FPDFPageObj_CreateTextObj(nint document, nint font, float fontSize);

    [LibraryImport(Library)]
    private static partial int FPDFText_SetText(nint textObject, ushort* text);

    [LibraryImport(Library)]
    private static partial void FPDFPageObj_Transform(nint pageObject, double a, double b, double c, double d, double e, double f);
}
