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

    /// <summary>
    /// One A4 page with a filled text field ("name": <paramref name="value"/>, in Helvetica at 100, 700) and a red square
    /// comment (a /Square annotation from 100, 400 to 200, 500), both with their appearance; with
    /// <paramref name="needAppearances"/>, the field has none and the form asks viewers to draw it. A value with letters
    /// outside ASCII is stored as PDF text in UTF-16, as form fillers store Vietnamese.
    /// </summary>
    public static void WriteFilledForm(string path, string value, bool needAppearances = false) =>
        WriteFilledForm(path, value, needAppearances, needAppearances ? null : value);

    /// <summary>
    /// As <see cref="WriteFilledForm(string, string, bool)"/>, the field's appearance drawing <paramref name="drawn"/> (in
    /// ASCII), or none when it is <c>null</c>: a form a viewer filled and drew, then marked for viewers to draw again.
    /// </summary>
    public static void WriteFilledForm(string path, string value, bool needAppearances, string? drawn)
    {
        var field = drawn is null ? "" : " /AP << /N 5 0 R >>";
        var fieldAppearance = Ascii($"/Tx BMC q BT /Helv 12 Tf 0 g 2 5 Td ({drawn}) Tj ET Q EMC");
        var square = "1 0 0 RG 4 w 2 2 96 96 re S"u8.ToArray();
        var text = value.All(char.IsAscii) ? $"({value})" : "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";
        Write(path,
        [
            Ascii("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >>"
                + (needAppearances ? " /NeedAppearances true" : "") + " >> >>"),
            Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Annots [4 0 R 7 0 R] >>"),
            Ascii($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V {text} /Rect [100 700 300 720] /P 3 0 R /F 4{field} /DA (/Helv 12 Tf 0 g) >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 6 0 R >> >>", fieldAppearance),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Ascii("<< /Type /Annot /Subtype /Square /Rect [100 400 200 500] /C [1 0 0] /F 4 /AP << /N 8 0 R >> >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]", square),
        ]);
    }

    /// <summary>What a page of <see cref="WriteScan"/> holds.</summary>
    public enum ScanPage
    {
        /// <summary>A scanned page of text: dark strokes on light grey.</summary>
        Text,

        /// <summary>The empty back of a sheet: light grey noise and three specks of dust.</summary>
        Blank,

        /// <summary>White, with one short line of real text in 6 pt Helvetica: almost no ink, but characters.</summary>
        OneSmallLine,

        /// <summary>A blank back with handwritten initials in a corner, about 1 x 0.6 cm of pen strokes: a picture, no characters.</summary>
        Initials,

        /// <summary>A blank back with a small stamp: a red square outline of 1.5 cm.</summary>
        Stamp,

        /// <summary>A blank back with the scanner lid's shadow along the left edge and specks of dust scattered over it.</summary>
        ShadowAndDust,

        /// <summary>A blank back with a comment (a sticky note) on it.</summary>
        Comment,
    }

    /// <summary>
    /// A two-sided scan as a scanner writes it: one page per entry, each about A4 (page n is 590 + n points wide, so a
    /// test can tell the pages apart), a scanned page being a greyscale image over the whole page.
    /// </summary>
    public static void WriteScan(string path, params ScanPage[] pages)
    {
        const int Width = 124, Height = 175; // about 15 DPI: each image pixel is a 10 x 10 block at 150 DPI
        var random = new Random(6);
        // 1 catalog, 2 page tree, 3 the font; then each page, its image and its content.
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Count " + pages.Length + " /Kids ["
                + string.Join(" ", Enumerable.Range(0, pages.Length).Select(i => (4 + (3 * i)) + " 0 R")) + "] >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        };
        for (var i = 0; i < pages.Length; i++)
        {
            var page = 4 + (3 * i);
            var width = 591 + i;
            objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} 842] /Resources << /XObject << /Im0 {page + 1} 0 R >> /Font << /F1 3 0 R >> >> /Contents {page + 2} 0 R{(pages[i] == ScanPage.Comment ? " /Annots [<< /Type /Annot /Subtype /Text /Rect [500 60 520 80] /Contents (Ky lai) /Open false >>]" : "")} >>")));
            var pixels = new byte[Width * Height];
            for (var at = 0; at < pixels.Length; at++)
                pixels[at] = (byte)random.Next(248, 256);
            if (pages[i] == ScanPage.Text)
            {
                // Lines of words: every eighth row, dark runs.
                for (var y = 10; y < Height - 10; y += 8)
                {
                    for (var x = 10; x < Width - 10; x++)
                    {
                        if (random.Next(3) > 0)
                            pixels[(y * Width) + x] = (byte)random.Next(0, 90);
                    }
                }
            }

            objects.Add(Stream(string.Create(CultureInfo.InvariantCulture, $"/Type /XObject /Subtype /Image /Width {Width} /Height {Height} /ColorSpace /DeviceGray /BitsPerComponent 8"), pixels));
            // Marks drawn over the scan as vectors, at their real size: a 1 pt speck is two pixels at 150 DPI.
            var dust = string.Concat(Enumerable.Range(0, pages[i] == ScanPage.ShadowAndDust ? 40 : 3).Select(_ =>
                string.Create(CultureInfo.InvariantCulture, $" {random.Next(30, width - 30)} {random.Next(30, 812)} 1 1 re f")));
            var content = pages[i] switch
            {
                ScanPage.OneSmallLine => "BT /F1 6 Tf 72 400 Td (Ghi chu) Tj ET",
                ScanPage.Text => string.Create(CultureInfo.InvariantCulture, $"q {width} 0 0 842 0 0 cm /Im0 Do Q"),
                ScanPage.Initials => Scanned("0 g 1.5 w 500 60 m 500 77 l 512 60 l 512 77 l S 518 60 m 518 77 l 528 60 l S 530 60 m 530 77 l S"),
                ScanPage.Stamp => Scanned("0.8 0 0 RG 1.5 w 100 100 42 42 re S"),
                ScanPage.ShadowAndDust => Scanned("0.35 g 0 0 1.5 842 re f 0.15 g" + dust),
                _ => Scanned("0.15 g" + dust),
            };
            objects.Add(Stream("", Ascii(content)));
            string Scanned(string marks) => string.Create(CultureInfo.InvariantCulture, $"q {width} 0 0 842 0 0 cm /Im0 Do Q {marks}");
        }

        Write(path, objects);
    }

    /// <summary>
    /// One page per entry, in its own size and /Rotate: its left half (in its own space) filled red, its right half blue,
    /// so a test can see which way up it is shown.
    /// </summary>
    public static void WriteHalves(string path, params (double Width, double Height, int Rotate)[] pages)
    {
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Count " + pages.Length + " /Kids ["
                + string.Join(" ", Enumerable.Range(0, pages.Length).Select(i => (3 + (2 * i)) + " 0 R")) + "] >>"),
        };
        for (var i = 0; i < pages.Length; i++)
        {
            var (width, height, rotate) = pages[i];
            objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Rotate {rotate} /Resources << >> /Contents {4 + (2 * i)} 0 R >>")));
            objects.Add(Stream("", Ascii(string.Create(CultureInfo.InvariantCulture,
                $"1 0 0 rg 0 0 {width / 2} {height} re f 0 0 1 rg {width / 2} 0 {width / 2} {height} re f"))));
        }

        Write(path, objects);
    }

    /// <summary>
    /// Three pages of pictures: a JPEG (DCT, as <paramref name="jpeg"/>) on pages 1 and 3, a 40 x 36 RGB image packed
    /// with Flate on page 2, and a 16 x 16 icon on every page.
    /// </summary>
    public static void WriteImages(string path, byte[] jpeg, int jpegWidth, int jpegHeight)
    {
        var rgb = new byte[40 * 36 * 3];
        for (var at = 0; at < rgb.Length; at += 3)
            (rgb[at], rgb[at + 1], rgb[at + 2]) = ((byte)0, (byte)160, (byte)0);
        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(rgb);
        var icon = Enumerable.Repeat((byte)128, 16 * 16).ToArray();
        const string Resources = "/Resources << /XObject << /Photo 3 0 R /Chart 4 0 R /Icon 5 0 R >> >>";
        Write(path,
        [
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Count 3 /Kids [6 0 R 7 0 R 8 0 R] >>"),
            Stream(string.Create(CultureInfo.InvariantCulture, $"/Type /XObject /Subtype /Image /Width {jpegWidth} /Height {jpegHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode"), jpeg),
            Stream("/Type /XObject /Subtype /Image /Width 40 /Height 36 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", packed.ToArray()),
            Stream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 8", icon),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] {Resources} /Contents 9 0 R >>"),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] {Resources} /Contents 10 0 R >>"),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] {Resources} /Contents 9 0 R >>"),
            Stream("", Ascii("q 200 0 0 150 10 10 cm /Photo Do Q q 16 0 0 16 300 250 cm /Icon Do Q")),
            Stream("", Ascii("q 200 0 0 150 10 10 cm /Chart Do Q q 16 0 0 16 300 250 cm /Icon Do Q")),
        ]);
    }

    /// <summary>
    /// <paramref name="pages"/> pages of 300 x 400 points, each drawing one image of <paramref name="sharedBytes"/> bytes
    /// of noise that every page shares, and one of <paramref name="ownBytes"/> of its own. Objects: 1 catalog, 2 page
    /// tree, 3 the shared image, then per page: the page, its own image, its content.
    /// </summary>
    public static void WriteSharedImagePages(string path, int pages, int sharedBytes, int ownBytes)
    {
        var random = new Random(5);
        byte[] Image(int bytes, out int side)
        {
            side = (int)Math.Ceiling(Math.Sqrt(bytes / 3.0));
            var pixels = new byte[side * side * 3];
            random.NextBytes(pixels);
            return pixels;
        }

        var shared = Image(sharedBytes, out var sharedSide);
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Count {pages} /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => (4 + (3 * i)) + " 0 R"))}] >>"),
            Stream($"/Type /XObject /Subtype /Image /Width {sharedSide} /Height {sharedSide} /ColorSpace /DeviceRGB /BitsPerComponent 8", shared),
        };
        for (var i = 0; i < pages; i++)
        {
            var page = 4 + (3 * i);
            var own = Image(ownBytes, out var ownSide);
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /XObject << /Im0 3 0 R /Im1 {page + 1} 0 R >> >> /Contents {page + 2} 0 R >>"));
            objects.Add(Stream($"/Type /XObject /Subtype /Image /Width {ownSide} /Height {ownSide} /ColorSpace /DeviceRGB /BitsPerComponent 8", own));
            objects.Add(Stream("", "q 300 0 0 200 0 0 cm /Im0 Do Q q 300 0 0 200 0 200 cm /Im1 Do Q"u8.ToArray()));
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
