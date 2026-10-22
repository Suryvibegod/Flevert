using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Flevert.Core;
using ImageMagick;

namespace Flevert.Image.Tests;

public sealed class ImageToTextTests : IDisposable
{
    private static readonly string HeicFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op() => ImageOperations.All.Single(op => op.Id == "image.to-text");

    [Fact]
    public void Menu_test_shows_text_instead_of_bmp_and_is_last_in_convert_to()
    {
        Assert.DoesNotContain(ImageOperations.All, op => op.Id == "image.to-bmp");

        var textOp = Op();
        Assert.Equal("Convert to/Text", textOp.MenuPath);
        Assert.Equal(
            ImageFile.ReadableExtensions.Order(StringComparer.Ordinal),
            textOp.Extensions.Order(StringComparer.Ordinal));

        var convertEntries = ImageOperations.All
            .Where(op => op.MenuPath.StartsWith("Convert to/", StringComparison.Ordinal))
            .Select(op => op.MenuPath["Convert to/".Length..])
            .ToList();

        Assert.DoesNotContain("BMP", convertEntries, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Text", convertEntries.Last());
    }

    [Fact]
    public async Task Png_with_Vietnamese_text_extracts_to_txt_with_high_similarity()
    {
        const string text = "Cộng hòa xã hội chủ nghĩa Việt Nam";
        var input = _dir.Combine("document.png");
        CreateTextImage(input, text, "Arial", fontSize: 24, width: 2400, height: 400, dpi: 300);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("document.txt"), output);
        Assert.True(File.Exists(output));

        var bytes = await File.ReadAllBytesAsync(output);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // UTF-8 BOM

        var content = Encoding.UTF8.GetString(bytes[3..]);
        Assert.EndsWith("\r\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\r", content, StringComparison.Ordinal);

        var sim = Similarity(text, content);
        Assert.True(sim >= 0.95, $"Expected similarity >= 0.95, got {sim:P1} for text: '{content.Trim()}'");
    }

    [Fact]
    public async Task Jpg_with_Vietnamese_text_extracts_to_txt_with_high_similarity()
    {
        const string text = "Hóa đơn bán hàng số 12345";
        var input = _dir.Combine("invoice.jpg");
        CreateTextImage(input, text, "Times New Roman", fontSize: 26, width: 2400, height: 400, dpi: 300, format: ImageFormat.Jpeg);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("invoice.txt"), output);
        Assert.True(File.Exists(output));

        var bytes = await File.ReadAllBytesAsync(output);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);

        var content = Encoding.UTF8.GetString(bytes[3..]);
        var sim = Similarity(text, content);
        Assert.True(sim >= 0.95, $"Expected similarity >= 0.95, got {sim:P1} for text: '{content.Trim()}'");
    }

    [Fact]
    public async Task Small_96_dpi_screenshot_with_14_px_text_still_reads_due_to_upscale()
    {
        const string text = "Hệ thống Flevert chuyển đổi ảnh";
        var input = _dir.Combine("screenshot.png");
        // Width 600, height 150: shorter side (150 px) < 1000 px, 14 px font, 96 DPI
        CreateTextImage(input, text, "Arial", fontSize: 14, width: 600, height: 150, dpi: 96);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("screenshot.txt"), output);
        var content = await File.ReadAllTextAsync(output);
        var sim = Similarity(text, content);
        Assert.True(sim >= 0.95, $"Expected similarity >= 0.95, got {sim:P1} for screenshot text: '{content.Trim()}'");
    }

    [Fact]
    public async Task Photo_rotated_90_degrees_via_pixels_reads_right()
    {
        const string paragraph = """
            Cộng hòa xã hội chủ nghĩa Việt Nam
            Độc lập - Tự do - Hạnh phúc
            Hóa đơn bán hàng kiêm phiếu xuất kho
            Ngày 21 tháng 10 năm 2026
            Khách hàng: Công ty Cổ phần Công nghệ Mới
            Địa chỉ: Số 123 Đường Giải Phóng, Quận Hai Bà Trưng, Hà Nội
            Mã số thuế: 0101234567
            Số tài khoản: 1902837465 tại Ngân hàng Ngoại thương Việt Nam
            Sản phẩm chuyển đổi tài liệu Flevert phiên bản nâng cấp v1.4.0
            Hệ thống nhận dạng ký tự quang học Tesseract Engine chạy an toàn
            Cộng hòa xã hội chủ nghĩa Việt Nam
            Độc lập - Tự do - Hạnh phúc
            """;

        var uprightPath = _dir.Combine("upright.png");
        CreateMultiLineImage(uprightPath, paragraph, "Arial", fontSize: 18, width: 2400, height: 2400, dpi: 300);

        var rotatedPath = _dir.Combine("photo_rotated.png");
        using (var bmp = new Bitmap(uprightPath))
        {
            bmp.RotateFlip(RotateFlipType.Rotate90FlipNone);
            bmp.Save(rotatedPath, ImageFormat.Png);
        }

        var output = await Op().RunAsync(rotatedPath, CancellationToken.None);

        Assert.Equal(_dir.Combine("photo_rotated.txt"), output);
        var content = await File.ReadAllTextAsync(output);
        var sim = Similarity(paragraph, content);
        Assert.True(sim >= 0.95, $"Expected similarity >= 0.95, got {sim:P1} for rotated text: '{content.Trim()}'");
    }

    [Fact]
    public async Task Blank_picture_is_skipped_with_message_and_leaves_no_file()
    {
        var input = _dir.Combine("blank.png");
        using (var image = new MagickImage(MagickColors.White, 200, 200))
            image.Write(input, MagickFormat.Png);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));
        Assert.Equal("No text was found in this picture.", ex.Message);
        Assert.False(File.Exists(_dir.Combine("blank.txt")));
    }

    [Fact]
    public async Task Heic_fixture_is_decoded_safely_and_skipped_when_blank()
    {
        var input = _dir.Combine("photo.heic");
        File.Copy(HeicFixture, input);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));
        Assert.Equal("No text was found in this picture.", ex.Message);
        Assert.False(File.Exists(_dir.Combine("photo.txt")));
    }

    [Fact]
    public async Task Cancel_leaves_no_txt_file()
    {
        const string text = "Văn bản mẫu để kiểm tra tính năng hủy";
        var input = _dir.Combine("cancel_test.png");
        CreateTextImage(input, text, "Arial", fontSize: 22, width: 2000, height: 400, dpi: 300);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op().RunAsync(input, cts.Token));

        Assert.False(File.Exists(_dir.Combine("cancel_test.txt")));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.partial"));
    }

    private static void CreateTextImage(
        string path,
        string text,
        string fontName,
        float fontSize,
        int width,
        int height,
        int dpi,
        ImageFormat? format = null)
    {
        using var bmp = new Bitmap(width, height);
        bmp.SetResolution(dpi, dpi);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Point);
            using var brush = new SolidBrush(Color.Black);
            g.DrawString(text, font, brush, 40, (height - fontSize * 2) / 2);
        }

        bmp.Save(path, format ?? ImageFormat.Png);
    }

    private static void CreateMultiLineImage(
        string path,
        string text,
        string fontName,
        float fontSize,
        int width,
        int height,
        int dpi)
    {
        using var bmp = new Bitmap(width, height);
        bmp.SetResolution(dpi, dpi);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Point);
            using var brush = new SolidBrush(Color.Black);
            g.DrawString(text, font, brush, new RectangleF(40, 40, width - 80, height - 80));
        }

        bmp.Save(path, ImageFormat.Png);
    }

    private static double Similarity(string expected, string actual)
    {
        var e = Normalize(expected);
        var a = Normalize(actual);
        if (e == a)
            return 1.0;
        if (e.Length == 0 || a.Length == 0)
            return 0.0;
        var dist = Levenshtein(e, a);
        return 1.0 - (double)dist / Math.Max(e.Length, a.Length);
    }

    private static string Normalize(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static int Levenshtein(string s, string t)
    {
        if (s == t)
            return 0;
        if (s.Length == 0)
            return t.Length;
        if (t.Length == 0)
            return s.Length;

        var v0 = new int[t.Length + 1];
        var v1 = new int[t.Length + 1];

        for (var i = 0; i <= t.Length; i++)
            v0[i] = i;

        for (var i = 0; i < s.Length; i++)
        {
            v1[0] = i + 1;
            for (var j = 0; j < t.Length; j++)
            {
                var cost = s[i] == t[j] ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }

            Array.Copy(v1, v0, v0.Length);
        }

        return v1[t.Length];
    }
}
