using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using Microsoft.Win32.SafeHandles;
using Flevert.Image;

namespace Flevert.Pdf;

/// <summary>
/// The worker side of the v0.2.2 PDF tools that need pdfium (docs/specs/pdf.md, "v0.2.2"): what <c>flevert.exe
/// pdf-worker</c> does for the requests <see cref="PdfWorkerTask.BlankPages"/>, <see cref="PdfWorkerTask.Edit"/> and
/// <see cref="PdfWorkerTask.ExtractImages"/>. Each says <c>pages &lt;count&gt;</c>, then <c>page &lt;n&gt; &lt;width&gt;
/// &lt;height&gt;</c> and <c>wrote &lt;n&gt;</c> around the work on each page, and <c>done &lt;count&gt;</c> at the end,
/// as the other requests do (<see cref="PdfRenderWorker"/>). On top of those lines, which do not change:
/// <list type="bullet">
/// <item>blank pages: <c>blank &lt;n&gt;</c> before <c>wrote &lt;n&gt;</c> when page n is blank;</item>
/// <item>edit: <c>saved &lt;megabytes&gt;</c> every 16 MB of the PDF written once every page is done, so a long save is
/// not taken for a stall;</item>
/// <item>extract images: once every page is looked at, <c>images &lt;count&gt;</c>, then for each image
/// <c>extract &lt;i&gt; &lt;width&gt; &lt;height&gt;</c> before it is written and <c>extracted &lt;i&gt; jpg|png</c>
/// after.</item>
/// </list>
/// A PDF the tool has nothing to do for ends with <c>error Skipped &lt;message&gt;</c>.
/// </summary>
internal static class PdfToolsWorker
{
    /// <summary>The resolution pages are looked at for blankness (Stirling-PDF's BlankPageController draws them at 150 DPI too).</summary>
    public const double BlankDpi = 150;

    /// <summary>A pixel at least this light is white: 255 less Stirling-PDF's default threshold of 10.</summary>
    internal const int WhiteLevel = 245;

    /// <summary>The band along the edges a blank page may have marks in (a scanner's shadow, the lid's edge): 5 mm.</summary>
    internal const double BlankMarginMillimetres = 5;

    /// <summary>The side of the squares the rest of the page is looked at in: 1 cm, about 59 pixels at 150 DPI.</summary>
    internal const double BlankTileCentimetres = 1;

    /// <summary>A square with more than this many dark pixels in a thousand holds a mark: 1.5%.</summary>
    internal const int MaxDarkPerThousand = 15;

    /// <summary>The smallest square, in pixels: an 8 x 8 square needs one dark pixel to pass 1.5%.</summary>
    private const double MinTilePixels = 8;

    /// <summary>A4 in points, portrait.</summary>
    internal const double A4Width = 595.28;
    internal const double A4Height = 841.89;

    /// <summary>A page within this many points of A4 is A4 (docs/specs/pdf.md: ±1 pt).</summary>
    internal const double A4Tolerance = 1;

    /// <summary>Page numbers: Helvetica, one of PDF's standard fonts (not embedded), 10 pt, about 1 cm from the edges.</summary>
    internal const float NumberSize = 10;
    internal const double NumberMargin = 28;

    /// <summary>The watermark's grey and opacity (30%), and its angle.</summary>
    private const uint WatermarkGray = 128;
    private const uint WatermarkAlpha = 77;
    private const double WatermarkAngle = Math.PI / 4;

    /// <summary>At most this many copies of the watermark on a page, however small the text is against the page.</summary>
    private const int MaxWatermarkTiles = 300;

    /// <summary>At most this many copies each side of the middle, along each axis.</summary>
    private const int MaxWatermarkTilesPerAxis = 1000;

    /// <summary>
    /// The largest side of a page PDF allows, in its units (200 inches; ISO 32000-1, annex C): what the edit tools work on.
    /// A page larger than that is refused before it is measured, so no size computed from it can overflow.
    /// </summary>
    internal const double MaxPageSide = 14_400;

    /// <summary>The space around each page on a sheet of several (points).</summary>
    private const double SheetMargin = 10;

    private const long SavedEvery = 16L << 20;

    internal const string NoBlankPagesMessage = "This PDF has no blank pages.";
    internal const string AlreadyA4Message = "Every page of this PDF is already A4.";
    internal const string NoImagesMessage = "This PDF has no images to extract.";

    /// <summary>Images smaller than this on a side (icons, rules) are not extracted.</summary>
    internal const int MinImageSide = 32;

    // ---- Blank pages -------------------------------------------------------------------------------------------------

    public static void FindBlankPages(PdfRenderRequest request, SafeFileHandle file, StreamWriter lines)
    {
        using var pdf = PdfDocument.Open(file, PdfLimits.Default);
        var total = Start(pdf, lines);
        byte[] pixels = [];
        for (var index = 0; index < total; index++)
        {
            var page = index + 1;
            var size = pdf.GetRenderSize(index, request.Dpi);
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {page} {size.Width} {size.Height}"));
            // A page with a character or an annotation (a comment, a stamp, a form field) is kept, however little ink it
            // shows; the others are judged by their pixels, which is all a scan has.
            if (pdf.CountChars(index) == 0 && pdf.CountAnnotations(index) == 0)
            {
                if (pixels.Length < size.Bytes)
                {
                    pixels = [];
                    pixels = GC.AllocateUninitializedArray<byte>(checked((int)size.Bytes));
                }

                pdf.RenderInto(index, request.Dpi, pixels);
                if (IsBlank(pixels.AsSpan(0, (int)size.Bytes), size.Width, size.Height, size.Dpi))
                    lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"blank {page}"));
            }

            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {page}"));
        }

        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done {total}"));
    }

    /// <summary>
    /// Whether BGRA pixels (<paramref name="width"/> by <paramref name="height"/>, rows without padding, drawn on white at
    /// <paramref name="dpi"/>) are a blank page. A pixel is dark below <see cref="WhiteLevel"/> (Stirling-PDF's
    /// BlankPageController, MIT: threshold 10). Stirling-PDF then counts dark pixels over the whole page (blank at 99.9%
    /// white), which drops a page whose only mark is small: handwritten initials, a stamp, a pencil note are under 0.1% of
    /// an A4 page. Flevert looks instead at squares of about <see cref="BlankTileCentimetres"/> cm, leaving out a band of
    /// <see cref="BlankMarginMillimetres"/> mm along the edges where scanners leave shadows: the page is blank only if no
    /// square has more than <see cref="MaxDarkPerThousand"/> dark pixels in a thousand. Dust is scattered and stays under
    /// that in every square; writing is concentrated, and one stroke through a square passes it.
    /// </summary>
    internal static bool IsBlank(ReadOnlySpan<byte> bgra, int width, int height, double dpi)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
            throw new ArgumentException("The pixels do not fill the size given.", nameof(bgra));
        var margin = (int)Math.Round(dpi * BlankMarginMillimetres / 25.4);
        // A page too small for the band is looked at whole.
        var (left, top, right, bottom) = width > 2 * margin && height > 2 * margin
            ? (margin, margin, width - margin, height - margin)
            : (0, 0, width, height);
        // A page so large it is drawn at a fraction of a pixel per centimetre: squares of a few pixels, not of a fraction.
        var tile = Math.Max(MinTilePixels, dpi * BlankTileCentimetres / 2.54);
        // Squares of about a centimetre that share out the area exactly: none is a sliver whose one speck counts for much.
        var columns = Math.Max(1, (int)Math.Round((right - left) / tile));
        var rows = Math.Max(1, (int)Math.Round((bottom - top) / tile));
        var columnOf = new int[right - left];
        for (var x = 0; x < columnOf.Length; x++)
            columnOf[x] = (int)((long)x * columns / columnOf.Length);
        var dark = new int[columns];
        for (var row = 0; row < rows; row++)
        {
            var (from, to) = (top + (int)((long)row * (bottom - top) / rows), top + (int)((long)(row + 1) * (bottom - top) / rows));
            Array.Clear(dark);
            for (var y = from; y < to; y++)
            {
                var line = bgra.Slice(((y * width) + left) * 4, (right - left) * 4);
                for (var x = 0; x < columnOf.Length; x++)
                {
                    var at = x * 4;
                    // ITU-R BT.601 luma, in thousandths: blue, green, red.
                    var luma = ((line[at] * 114) + (line[at + 1] * 587) + (line[at + 2] * 299)) / 1000;
                    if (luma < WhiteLevel)
                        dark[columnOf[x]]++;
                }
            }

            for (var column = 0; column < columns; column++)
            {
                var tileWidth = (int)((long)(column + 1) * columnOf.Length / columns) - (int)((long)column * columnOf.Length / columns);
                if ((long)dark[column] * 1000 > (long)MaxDarkPerThousand * tileWidth * (to - from))
                    return false;
            }
        }

        return true;
    }

    // ---- Edits ---------------------------------------------------------------------------------------------------

    public static void Edit(PdfRenderRequest request, SafeFileHandle file, StreamWriter lines)
    {
        var edit = PdfEdit.Decode(request.Options); // checked when the request was read
        var font = edit.Kind == PdfEditKind.Watermark ? ReadArial(edit.Font) : null;
        using var pdf = PdfDocument.Open(file, PdfLimits.Default);
        var total = Start(pdf, lines);
        for (var index = 0; index < total; index++)
        {
            var size = pdf.GetPageSize(index);
            if (!(size.Width <= MaxPageSide && size.Height <= MaxPageSide))
                throw new PdfException(PdfError.TooLarge, string.Create(CultureInfo.InvariantCulture, $"Page {index + 1} of this PDF is larger than PDF allows (200 inches on a side), so Flevert cannot change it."));
        }

        // What pdfium writes is about the PDF (its pages and what they share, once), and the font of a watermark.
        var needed = RandomAccess.GetLength(file) + (font?.Length ?? 0) + PdfRenderBudget.MarginBytes;
        switch (edit.Kind)
        {
            case PdfEditKind.PageNumbers:
            case PdfEditKind.Watermark:
                EditPages(pdf, total, edit, font, lines);
                RequireRoom(request.Folder, needed);
                Save(pdf.Handle, request.Folder, lines);
                break;
            default:
                using (var sheets = Arrange(pdf, total, edit.Kind, lines))
                {
                    RequireRoom(request.Folder, needed);
                    Save(sheets, request.Folder, lines);
                }

                break;
        }

        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done {total}"));
    }

    /// <summary>
    /// The watermark's font, the file the parent found (<see cref="SystemFont"/>: Windows' Arial,
    /// <c>%WINDIR%\Fonts\arial.ttf</c>, or the TrueType font fontconfig gives for it), which has the Vietnamese letters;
    /// Flevert ships no font.
    /// </summary>
    private static byte[] ReadArial(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PdfRenderWorker.SetupException(PdfWatermark.NoArialMessage, e);
        }
    }

    private static unsafe void EditPages(PdfDocument pdf, int total, PdfEdit edit, byte[]? fontData, StreamWriter lines)
    {
        var document = pdf.Handle;
        nint font = 0;
        try
        {
            TextMetrics metrics;
            lock (Pdfium.Gate)
            {
                if (fontData is not null)
                {
                    fixed (byte* data = fontData)
                        font = PdfiumNative.FPDFText_LoadFont(document.DangerousGetHandle(), data, (uint)fontData.Length, PdfiumNative.FontTrueType, cid: 1);
                    if (font == 0)
                        throw new PdfRenderWorker.SetupException("Flevert could not load Windows' Arial font for the watermark.", null);
                }

                // The text's width and height at 100 pt, scaled for each page.
                metrics = edit.Kind == PdfEditKind.Watermark ? Measure(document, font, edit.Text) : default;
            }

            for (var index = 0; index < total; index++)
            {
                var page = index + 1;
                lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {page} 1 1"));
                lock (Pdfium.Gate)
                {
                    using var handle = PageHandle.Load(document, index);
                    var native = handle.DangerousGetHandle();
                    var geometry = PageGeometry.Of(native, page);
                    if (edit.Kind == PdfEditKind.PageNumbers)
                        AddNumber(document, native, geometry, NumberText(edit.Style, page, total), edit.Position);
                    else
                        AddWatermark(document, native, geometry, font, edit.Text, metrics);
                    if (PdfiumNative.FPDFPage_GenerateContent(native) == 0)
                        throw new PdfException(PdfError.DamagedPage, $"Page {page} of this PDF is damaged and cannot be changed.");
                }

                lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {page}"));
            }
        }
        finally
        {
            if (font != 0)
            {
                lock (Pdfium.Gate)
                    PdfiumNative.FPDFFont_Close(font);
            }
        }
    }

    /// <summary>"1", "1 / 12" or "Page 1 of 12".</summary>
    internal static string NumberText(PdfPageNumberStyle style, int page, int total) => style switch
    {
        PdfPageNumberStyle.OfTotal => string.Create(CultureInfo.InvariantCulture, $"{page} / {total}"),
        PdfPageNumberStyle.Words => string.Create(CultureInfo.InvariantCulture, $"Page {page} of {total}"),
        _ => page.ToString(CultureInfo.InvariantCulture),
    };

    private static unsafe void AddNumber(DocumentHandle document, nint page, PageGeometry geometry, string text, PdfPageNumberPosition position)
    {
        nint item;
        fixed (byte* helvetica = "Helvetica\0"u8)
            item = PdfiumNative.FPDFPageObj_NewTextObj(document.DangerousGetHandle(), helvetica, NumberSize);
        if (item == 0)
            throw new InvalidOperationException("pdfium could not make a text object.");
        var bounds = SetText(item, text);
        SetFill(item, 0, 0, 0, 255);
        // About 1 cm from the edges, less on a page too small for that.
        var margin = Math.Min(NumberMargin, Math.Min(geometry.Width, geometry.Height) / 6);
        var width = bounds.Right - bounds.Left;
        var (x, y) = position switch
        {
            PdfPageNumberPosition.BottomRight => (geometry.Width - margin - width, margin),
            PdfPageNumberPosition.TopRight => (geometry.Width - margin - width, geometry.Height - margin - bounds.Top),
            _ => ((geometry.Width - width) / 2, margin),
        };
        Place(page, item, geometry.ToUser(Affine.Translation(x - bounds.Left, y)));
    }

    // The text's extent at 100 pt from its origin, in Arial: for the watermark's size and spacing on each page.
    private readonly record struct TextMetrics(double Width, double Middle);

    private static unsafe TextMetrics Measure(DocumentHandle document, nint font, string text)
    {
        var probe = PdfiumNative.FPDFPageObj_CreateTextObj(document.DangerousGetHandle(), font, 100);
        if (probe == 0)
            throw new InvalidOperationException("pdfium could not make a text object.");
        try
        {
            var bounds = SetText(probe, text);
            return new TextMetrics(Math.Max(1, bounds.Right - bounds.Left), (bounds.Top + bounds.Bottom) / 2);
        }
        finally
        {
            PdfiumNative.FPDFPageObj_Destroy(probe);
        }
    }

    /// <summary>
    /// The watermark: grey, 30% opaque, at 45°, repeated across the whole page as it is shown (one mark on a scanned ID
    /// is easy to cut away). Its size follows the page: about a fourteenth of its shorter side, smaller when the text is
    /// long, and the copies are spaced by the text's length and a few times its height.
    /// </summary>
    private static void AddWatermark(DocumentHandle document, nint page, PageGeometry geometry, nint font, string text, TextMetrics metrics)
    {
        var (width, height) = (geometry.Width, geometry.Height);
        var diagonal = Math.Sqrt((width * width) + (height * height));
        var size = Math.Clamp(Math.Min(width, height) / 14, 6, 300);
        if (metrics.Width * size / 100 > diagonal * 0.9)
            size = Math.Max(1, diagonal * 0.9 * 100 / metrics.Width);
        var textWidth = metrics.Width * size / 100;
        var middle = metrics.Middle * size / 100;
        var (stepX, stepY) = (textWidth + (3 * size), 5 * size);
        // In doubles, clamped before they become counts: a page of absurd size must not overflow them.
        var (across, along) = (Math.Ceiling(diagonal / 2 / stepX) + 1, Math.Ceiling(diagonal / 2 / stepY) + 1);
        var tiles = ((2 * across) + 1) * ((2 * along) + 1);
        if (tiles > MaxWatermarkTiles)
        {
            var spread = Math.Sqrt(tiles / MaxWatermarkTiles);
            (stepX, stepY) = (stepX * spread, stepY * spread);
            (across, along) = (Math.Ceiling(diagonal / 2 / stepX) + 1, Math.Ceiling(diagonal / 2 / stepY) + 1);
        }

        var (columns, rows) = ((int)Math.Clamp(across, 1, MaxWatermarkTilesPerAxis), (int)Math.Clamp(along, 1, MaxWatermarkTilesPerAxis));

        var (cos, sin) = (Math.Cos(WatermarkAngle), Math.Sin(WatermarkAngle));
        var (centerX, centerY) = (width / 2, height / 2);
        for (var row = -rows; row <= rows; row++)
        {
            var shift = (row & 1) == 0 ? 0 : stepX / 2;
            for (var column = -columns; column <= columns; column++)
            {
                var (u, v) = ((column * stepX) + shift, row * stepY);
                // The copy's middle; one whose middle is farther from the page's than half the diagonal and half the
                // text cannot touch the page.
                var (x, y) = (centerX + (u * cos) - (v * sin), centerY + (u * sin) + (v * cos));
                if (Math.Sqrt(((x - centerX) * (x - centerX)) + ((y - centerY) * (y - centerY))) > (diagonal / 2) + (textWidth / 2))
                    continue;
                var originX = x - (textWidth / 2 * cos) + (middle * sin);
                var originY = y - (textWidth / 2 * sin) - (middle * cos);
                var item = PdfiumNative.FPDFPageObj_CreateTextObj(document.DangerousGetHandle(), font, (float)size);
                if (item == 0)
                    throw new InvalidOperationException("pdfium could not make a text object.");
                SetText(item, text);
                SetFill(item, WatermarkGray, WatermarkGray, WatermarkGray, WatermarkAlpha);
                Place(page, item, geometry.ToUser(new Affine(cos, sin, -sin, cos, originX, originY)));
            }
        }
    }

    // Sets the text and returns its bounds at the object's origin.
    private static unsafe (double Left, double Bottom, double Right, double Top) SetText(nint item, string text)
    {
        fixed (char* chars = text + "\0")
        {
            if (PdfiumNative.FPDFText_SetText(item, (ushort*)chars) == 0)
            {
                PdfiumNative.FPDFPageObj_Destroy(item);
                throw new InvalidOperationException("pdfium could not set the text.");
            }
        }

        float left, bottom, right, top;
        if (PdfiumNative.FPDFPageObj_GetBounds(item, &left, &bottom, &right, &top) == 0)
            return (0, 0, 0, 0);
        return (left, bottom, right, top);
    }

    private static void SetFill(nint item, uint red, uint green, uint blue, uint alpha)
    {
        if (PdfiumNative.FPDFPageObj_SetFillColor(item, red, green, blue, alpha) == 0)
        {
            PdfiumNative.FPDFPageObj_Destroy(item);
            throw new InvalidOperationException("pdfium could not set the colour of the text.");
        }
    }

    // Puts a new object on the page with the matrix; the page owns it from then on.
    private static void Place(nint page, nint item, Affine matrix)
    {
        PdfiumNative.FPDFPageObj_Transform(item, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
        if (PdfiumNative.FPDFPage_InsertObject(page, item) == 0)
        {
            PdfiumNative.FPDFPageObj_Destroy(item);
            throw new InvalidOperationException("pdfium could not add an object to the page.");
        }
    }

    // ---- Sheets: n-up and fit to A4 --------------------------------------------------------------------------------

    /// <summary>
    /// A new PDF of A4 sheets: 2 pages side by side on landscape A4, 4 two by two on portrait A4 (in reading order), or
    /// each page scaled onto A4 in its own orientation, centred, keeping its proportions; an A4 page goes the same way, at
    /// its size. Each page's form fields and comments are flattened into it first, and any annotation left (a link) is not
    /// kept: the result is for printing. pdfium copies every page in one run (<c>FPDF_ImportNPagesToOne</c>), so what
    /// pages share (fonts, images) is copied once. For several per sheet, pages come as forms on the sheets and each is
    /// moved inside a margin of its cell. For one per sheet, pdfium copies the pages as they are, and each page's content is
    /// scaled onto A4 where it is (<c>FPDFPage_TransFormWithClip</c>), its boxes made A4.
    /// </summary>
    private static DocumentHandle Arrange(PdfDocument pdf, int total, PdfEditKind kind, StreamWriter lines)
    {
        var sizes = Enumerable.Range(0, total).Select(pdf.GetPageSize).ToArray();
        if (kind == PdfEditKind.FitA4 && sizes.All(IsA4))
            throw new PdfRenderWorker.SkipException(AlreadyA4Message);

        var (columns, rows, sheetWidth, sheetHeight) = kind switch
        {
            PdfEditKind.TwoPerSheet => (2, 1, A4Height, A4Width),
            PdfEditKind.FourPerSheet => (2, 2, A4Width, A4Height),
            _ => (1, 1, A4Width, A4Height),
        };
        var perSheet = columns * rows;
        var margin = perSheet == 1 ? 0 : SheetMargin;
        DocumentHandle sheets;
        lock (Pdfium.Gate)
        {
            for (var index = 0; index < total; index++)
            {
                lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {index + 1} 1 1"));
                FlattenSource(pdf.Handle, index);
                lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {index + 1}"));
            }

            var native = PdfiumNative.FPDF_ImportNPagesToOne(pdf.Handle.DangerousGetHandle(), (float)sheetWidth, (float)sheetHeight, (nuint)columns, (nuint)rows);
            if (native == 0)
                throw new PdfException(PdfError.ReaderFailed, "Flevert's PDF reader could not lay out the pages: the PDF may be damaged.");
            sheets = new DocumentHandle(native, new NothingToRelease());
        }

        try
        {
            if (perSheet == 1)
            {
                for (var index = 0; index < total; index++)
                    FitOnA4(sheets, index);
                return sheets;
            }

            for (var sheet = 0; sheet * perSheet < total; sheet++)
            {
                lock (Pdfium.Gate)
                {
                    using var page = PageHandle.Load(sheets, sheet);
                    var native = page.DangerousGetHandle();
                    var first = sheet * perSheet;
                    var slots = Math.Min(perSheet, total - first);
                    if (PdfiumNative.FPDFPage_CountObjects(native) != slots)
                        throw new PdfException(PdfError.ReaderFailed, "Flevert's PDF reader could not lay out the pages: the PDF may be damaged.");
                    for (var slot = 0; slot < slots; slot++)
                    {
                        // Where pdfium put the page (its cell of the sheet it was given, scaled to fit, centred), and where
                        // it goes (its cell of this sheet, inside the margin): the same page, moved and scaled.
                        var size = sizes[first + slot];
                        var (fromX, fromY, fromScale) = Fit(size, sheetWidth, sheetHeight, columns, rows, slot, 0);
                        var (toX, toY, toScale) = Fit(size, sheetWidth, sheetHeight, columns, rows, slot, margin);
                        var scale = toScale / fromScale;
                        PdfiumNative.FPDFPageObj_Transform(PdfiumNative.FPDFPage_GetObject(native, slot), scale, 0, 0, scale, toX - (scale * fromX), toY - (scale * fromY));
                    }

                    if (PdfiumNative.FPDFPage_GenerateContent(native) == 0)
                        throw new InvalidOperationException("pdfium could not write a sheet.");
                }
            }

            return sheets;
        }
        catch
        {
            sheets.Dispose();
            throw;
        }
    }

    // Page index of a copy of the PDF: its content scaled onto A4 as it is shown (landscape for a page wider than tall),
    // centred, clipped to where the page was; its annotations removed (they would stay where they were); its boxes A4.
    private static unsafe void FitOnA4(DocumentHandle document, int index)
    {
        lock (Pdfium.Gate)
        {
            using var handle = PageHandle.Load(document, index);
            var page = handle.DangerousGetHandle();
            var shown = PageGeometry.Of(page, index + 1);
            var rotation = PdfiumNative.FPDFPage_GetRotation(page);
            var (width, height) = shown.Width > shown.Height ? (A4Height, A4Width) : (A4Width, A4Height);
            var (x, y, scale) = Fit(new PdfPageSize(shown.Width, shown.Height), width, height, 1, 1, 0, 0);
            // The new box, in the page's own space: turned a quarter, the page shows its box's height across.
            var (boxWidth, boxHeight) = (rotation & 1) == 1 ? (height, width) : (width, height);
            var target = PageGeometry.For(rotation, 0, 0, boxWidth, boxHeight);
            var placed = new Affine(scale, 0, 0, scale, x, y);
            var matrix = target.ToUser(placed).After(shown.FromShown.Inverse());
            var (left, bottom) = target.FromShown.Apply(x, y);
            var (right, top) = target.FromShown.Apply(x + (shown.Width * scale), y + (shown.Height * scale));
            var clip = new PdfiumNative.RectF
            {
                Left = (float)Math.Min(left, right),
                Right = (float)Math.Max(left, right),
                Bottom = (float)Math.Min(bottom, top),
                Top = (float)Math.Max(bottom, top),
            };
            var native = new PdfiumNative.Matrix
            {
                A = (float)matrix.A,
                B = (float)matrix.B,
                C = (float)matrix.C,
                D = (float)matrix.D,
                E = (float)matrix.E,
                F = (float)matrix.F,
            };
            for (var annotation = PdfiumNative.FPDFPage_GetAnnotCount(page) - 1; annotation >= 0; annotation--)
            {
                if (PdfiumNative.FPDFPage_RemoveAnnot(page, annotation) == 0)
                    throw new PdfException(PdfError.DamagedPage, $"Page {index + 1} of this PDF is damaged and cannot be copied.");
            }

            // A page with nothing drawn has no content to move (and pdfium refuses to).
            if (PdfiumNative.FPDFPage_CountObjects(page) > 0 && PdfiumNative.FPDFPage_TransFormWithClip(page, &native, &clip) == 0)
                throw new PdfException(PdfError.DamagedPage, $"Page {index + 1} of this PDF is damaged and cannot be copied.");
            var (w, h) = ((float)boxWidth, (float)boxHeight);
            PdfiumNative.FPDFPage_SetMediaBox(page, 0, 0, w, h);
            PdfiumNative.FPDFPage_SetCropBox(page, 0, 0, w, h);
            PdfiumNative.FPDFPage_SetBleedBox(page, 0, 0, w, h);
            PdfiumNative.FPDFPage_SetTrimBox(page, 0, 0, w, h);
            PdfiumNative.FPDFPage_SetArtBox(page, 0, 0, w, h);
        }
    }

    /// <summary>
    /// Where a page of <paramref name="size"/> goes in <paramref name="slot"/> of a sheet of <paramref name="columns"/> x
    /// <paramref name="rows"/> cells (in reading order, the first at the top left), inside <paramref name="margin"/>: its
    /// bottom left corner and its scale, the largest that fits, centred in the cell (as pdfium's N-up places pages).
    /// </summary>
    internal static (double X, double Y, double Scale) Fit(PdfPageSize size, double width, double height, int columns, int rows, int slot, double margin)
    {
        var (cellWidth, cellHeight) = (width / columns, height / rows);
        var (cellX, cellY) = ((slot % columns) * cellWidth, height - (((slot / columns) + 1) * cellHeight));
        var scale = Math.Min((cellWidth - (2 * margin)) / size.Width, (cellHeight - (2 * margin)) / size.Height);
        return (cellX + ((cellWidth - (size.Width * scale)) / 2), cellY + ((cellHeight - (size.Height * scale)) / 2), scale);
    }

    internal static bool IsA4(PdfPageSize size) =>
        (Math.Abs(size.Width - A4Width) <= A4Tolerance && Math.Abs(size.Height - A4Height) <= A4Tolerance)
        || (Math.Abs(size.Width - A4Height) <= A4Tolerance && Math.Abs(size.Height - A4Width) <= A4Tolerance);

    // Draws the page's form fields and comments into its content (for printing), so the form of the page shows them.
    private static void FlattenSource(DocumentHandle document, int index)
    {
        using var page = PageHandle.Load(document, index);
        if (PdfiumNative.FPDFPage_Flatten(page.DangerousGetHandle(), PdfiumNative.FlattenForPrint) == PdfiumNative.FlattenFailed)
            throw new PdfException(PdfError.DamagedPage, $"Page {index + 1} of this PDF is damaged and cannot be copied.");
    }

    private sealed class NothingToRelease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    // ---- Saving -------------------------------------------------------------------------------------------------

    private static void RequireRoom(string path, long needed)
    {
        if (PdfRenderBudget.FreeBytes(Path.GetDirectoryName(path)!) is { } free && needed > free)
        {
            throw new PdfRenderWorker.PageWriteException(
                $"There is not enough free space for the result: it needs about {PdfRenderBudget.Describe(needed)}, and {PdfRenderBudget.Describe(free)} is free.", null);
        }
    }

    // The whole document, never an update appended to the input, into a new file the parent named.
    private static unsafe void Save(DocumentHandle document, string path, StreamWriter lines)
    {
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var counted = new SavedLines(file, lines);
            lock (Pdfium.Gate)
            {
                using var sink = new StreamSink(counted);
                var saved = PdfiumNative.FPDF_SaveAsCopy(document.DangerousGetHandle(), sink.Write, PdfiumNative.SaveNoIncremental);
                sink.ThrowIfFailed();
                if (saved == 0)
                    throw new PdfException(PdfError.ReaderFailed, "Flevert's PDF reader could not write the result: the PDF may be damaged.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PdfRenderWorker.PageWriteException($"Could not write the PDF: {e.Message}", e);
        }
    }

    /// <summary>Passes what pdfium saves to the file, and says <c>saved &lt;MB&gt;</c> every 16 MB of it.</summary>
    private sealed class SavedLines(Stream inner, StreamWriter lines) : Stream
    {
        private long _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            var before = _written / SavedEvery;
            _written += buffer.Length;
            if (_written / SavedEvery != before)
                lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"saved {_written >> 20}"));
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // ---- Extract images ----------------------------------------------------------------------------------------

    public static void ExtractImages(PdfRenderRequest request, SafeFileHandle file, StreamWriter lines)
    {
        using var pdf = PdfDocument.Open(file, PdfLimits.Default);
        var total = Start(pdf, lines);
        var found = new List<(int Page, PdfPageImage Image, bool Jpeg)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < total; index++)
        {
            var page = index + 1;
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {page} 1 1"));
            foreach (var image in pdf.GetImages(index))
            {
                // Icons and rules are no pictures; an image drawn many times is written once.
                if (image.Width < MinImageSide || image.Height < MinImageSide || !seen.Add(image.Hash))
                    continue;
                found.Add((index, image, image.Filters is ["DCTDecode"]));
            }

            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {page}"));
        }

        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"images {found.Count}"));
        if (found.Count == 0)
            throw new PdfRenderWorker.SkipException(NoImagesMessage);

        const long MaxImagePixels = (long)PdfDocument.MaxRenderPixels * PdfDocument.MaxRenderPixels;
        long decodedPixels = 0, jpegBytes = 0;
        for (var i = 0; i < found.Count; i++)
        {
            var (_, image, jpeg) = found[i];
            if (jpeg)
            {
                jpegBytes += image.DataLength;
                continue;
            }

            var pixels = (long)image.Width * image.Height;
            if (pixels > MaxImagePixels)
            {
                throw new PdfException(PdfError.TooLarge, string.Create(CultureInfo.InvariantCulture,
                    $"Image {i + 1} of this PDF is larger than {MaxImagePixels / 1_000_000} megapixels, the most Flevert extracts."));
            }

            decodedPixels += pixels;
        }

        // As for pictures of pages (PdfRenderBudget): a PNG at about a byte a pixel, the JPEGs as they are, and a margin.
        if (decodedPixels > PdfRenderBudget.MaxTotalPixels)
            throw new PdfException(PdfError.TooLarge, "The images of this PDF are too large to extract: together they would be more than 5000 megapixels.");
        var needed = jpegBytes + decodedPixels + PdfRenderBudget.MarginBytes;
        if (PdfRenderBudget.FreeBytes(request.Folder) is { } free && needed > free)
        {
            throw new PdfRenderWorker.PageWriteException(
                $"There is not enough free space for the images of this PDF: they need about {PdfRenderBudget.Describe(needed)}, and {PdfRenderBudget.Describe(free)} is free.", null);
        }

        if (found.Any(entry => !entry.Jpeg))
        {
            try
            {
                MagickSetup.EnsureInitialized();
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or MagickException)
            {
                throw new PdfRenderWorker.SetupException($"Flevert could not set up its image encoder: {e.Message}", e);
            }
        }

        var stem = Path.GetFileNameWithoutExtension(request.InputPath);
        for (var i = 0; i < found.Count; i++)
        {
            var number = i + 1;
            var (page, image, jpeg) = found[i];
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"extract {number} {image.Width} {image.Height}"));
            // A JPEG goes out byte for byte, as it is stored in the PDF; anything else is decoded by pdfium, as a PNG.
            var data = jpeg ? pdf.GetImageData(page, image.Location) : null;
            if (data is not null)
            {
                WriteNew(Path.Combine(request.Folder, PdfToImages.PageName(stem, number, found.Count, PdfPageImageFormat.Jpeg)), data);
            }
            else
            {
                var pixels = pdf.GetImagePixels(page, image.Location)
                    ?? throw new PdfException(PdfError.DamagedPage, string.Create(CultureInfo.InvariantCulture,
                        $"Image {number} of this PDF, on page {page + 1}, is damaged and cannot be read."));
                WriteNew(Path.Combine(request.Folder, PdfToImages.PageName(stem, number, found.Count, PdfPageImageFormat.Png)), EncodePng(pixels));
            }

            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"extracted {number} {(data is not null ? "jpg" : "png")}"));
        }

        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done {total}"));
    }

    private static byte[] EncodePng(PdfImagePixels pixels)
    {
        using var image = new MagickImage();
        // Raw pixels in memory, as for pages: no coder parses anything.
        var settings = pixels.Gray
            ? new PixelReadSettings((uint)pixels.Width, (uint)pixels.Height, StorageType.Char, "I")
            : new PixelReadSettings((uint)pixels.Width, (uint)pixels.Height, StorageType.Char, PixelMapping.BGR);
        image.ReadPixels(pixels.Pixels, settings);
        if (pixels.Gray)
            image.ColorType = ColorType.Grayscale;
        return image.ToByteArray(MagickFormat.Png);
    }

    // CreateNew: others who can write beside the input can add entries to the folder, and nothing they put there is ever
    // replaced (ADR-0007).
    private static void WriteNew(string path, byte[] data)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PdfRenderWorker.PageWriteException($"Could not write {Path.GetFileName(path)}: {e.Message}", e);
        }
    }

    // ---- Make searchable (OCR) ---------------------------------------------------------------------------------------

    public static void RenderOcrPages(PdfRenderRequest request, SafeFileHandle file, StreamWriter lines)
    {
        using var pdf = PdfDocument.Open(file, PdfLimits.Default);
        var total = Start(pdf, lines);

        long allPixels = 0;
        for (var index = 0; index < total; index++)
        {
            var size = pdf.GetRenderSize(index, request.Dpi);
            allPixels += (long)size.Width * size.Height;
        }

        if (PdfRenderBudget.Check(allPixels, PdfPageImageFormat.Png, PdfRenderBudget.FreeBytes(request.Folder)) is { } noRoom)
            throw new PdfRenderWorker.PageWriteException(noRoom, null);

        try
        {
            MagickSetup.EnsureInitialized();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or MagickException)
        {
            throw new PdfRenderWorker.SetupException($"Flevert could not set up its image encoder: {e.Message}", e);
        }

        byte[] pixels = [];
        for (var index = 0; index < total; index++)
        {
            var page = index + 1;
            var size = pdf.GetRenderSize(index, request.Dpi);
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {page} {size.Width} {size.Height}"));

            if (pdf.CountChars(index) <= 10)
            {
                if (pixels.Length < size.Bytes)
                {
                    pixels = [];
                    pixels = GC.AllocateUninitializedArray<byte>(checked((int)size.Bytes));
                }

                pdf.RenderInto(index, request.Dpi, pixels);
                var imagePath = Path.Combine(request.Folder, $"page-{page}.png");
                EncodeOcrPng(pixels.AsSpan(0, (int)size.Bytes), size, imagePath, PdfRenderWorker.Busy(lines, page));
                lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ocr {page}"));
            }

            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {page}"));
        }

        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done {total}"));
    }

    private static void EncodeOcrPng(ReadOnlySpan<byte> bgra, PdfRenderSize size, string path, Action busy)
    {
        using var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)size.Width, (uint)size.Height, StorageType.Char, PixelMapping.BGRA));
        image.Alpha(AlphaAction.Off);
        image.Density = new Density(size.Dpi, size.Dpi, DensityUnit.PixelsPerInch);
        image.ColorSpace = ColorSpace.Gray;
        image.ColorType = ColorType.Grayscale;
        image.Progress += (_, _) => busy();
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            image.Write(stream, MagickFormat.Png);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PdfRenderWorker.PageWriteException($"Could not write {Path.GetFileName(path)}: {e.Message}", e);
        }
    }

    // ---- Shared ---------------------------------------------------------------------------------------------------

    private static int Start(PdfDocument pdf, StreamWriter lines)
    {
        var total = pdf.PageCount;
        if (total == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");
        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"pages {total}"));
        return total;
    }
}

/// <summary>An affine matrix as PDF writes one: (x, y) goes to (a·x + c·y + e, b·x + d·y + f).</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static Affine Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    /// <summary><paramref name="first"/>, then this.</summary>
    public Affine After(Affine first) => new(
        (A * first.A) + (C * first.B),
        (B * first.A) + (D * first.B),
        (A * first.C) + (C * first.D),
        (B * first.C) + (D * first.D),
        (A * first.E) + (C * first.F) + E,
        (B * first.E) + (D * first.F) + F);

    public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>The matrix that undoes this one (which must not flatten the plane).</summary>
    public Affine Inverse()
    {
        var determinant = (A * D) - (B * C);
        return new Affine(D / determinant, -B / determinant, -C / determinant, A / determinant,
            ((C * F) - (D * E)) / determinant, ((B * E) - (A * F)) / determinant);
    }
}

/// <summary>
/// A page as it is shown, for placing marks on it: its shown size (after its /Rotate), and the matrix from that view,
/// origin at the bottom left, to the page's own space, where its content is drawn.
/// </summary>
internal readonly record struct PageGeometry(double Width, double Height, Affine FromShown)
{
    public static unsafe PageGeometry Of(nint page, int number)
    {
        PdfiumNative.RectF box;
        if (PdfiumNative.FPDF_GetPageBoundingBox(page, &box) == 0)
            throw new PdfException(PdfError.DamagedPage, $"Page {number} of this PDF has no valid size.");
        var (left, bottom) = (Math.Min(box.Left, box.Right), Math.Min(box.Bottom, box.Top));
        var (width, height) = (Math.Abs((double)box.Right - box.Left), Math.Abs((double)box.Top - box.Bottom));
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new PdfException(PdfError.DamagedPage, $"Page {number} of this PDF has no valid size.");
        return For(PdfiumNative.FPDFPage_GetRotation(page), left, bottom, width, height);
    }

    /// <summary>
    /// For a page whose box starts at (<paramref name="left"/>, <paramref name="bottom"/>), <paramref name="width"/> by
    /// <paramref name="height"/> in its own space, shown turned <paramref name="quarterTurns"/> quarter turns clockwise.
    /// </summary>
    internal static PageGeometry For(int quarterTurns, double left, double bottom, double width, double height) => (quarterTurns & 3) switch
    {
        // Turned clockwise, the page's left edge is shown at the top: a point (x, y) of the view is (w - y, x) of the box.
        1 => new(height, width, new Affine(0, 1, -1, 0, left + width, bottom)),
        2 => new(width, height, new Affine(-1, 0, 0, -1, left + width, bottom + height)),
        3 => new(height, width, new Affine(0, -1, 1, 0, left, bottom + height)),
        _ => new(width, height, new Affine(1, 0, 0, 1, left, bottom)),
    };

    /// <summary>A matrix that places something in the shown page, as one that places it in the page's own space.</summary>
    public Affine ToUser(Affine shown) => FromShown.After(shown);
}
