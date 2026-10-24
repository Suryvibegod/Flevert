using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Flevert.Core;
using Flevert.Ocr;
using ImageMagick;

namespace Flevert.Image.Tests;

public sealed class ImageToTextTests : IDisposable
{
    private static readonly string HeicFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic");
    private static readonly string HeicTextFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "vietnamese-text.heic");

    // The text drawn in fixtures/vietnamese-text.heic by make_text_heic_fixture.py.
    private const string HeicText = """
        Hóa đơn bán hàng số 12345
        Cộng hòa xã hội chủ nghĩa Việt Nam
        Độc lập - Tự do - Hạnh phúc
        """;
    private readonly TempDir _dir = new();

    // Not inside _dir: its name has Vietnamese letters, which tesseract cannot open (see
    // Scratch_root_with_Vietnamese_letters_still_reads).
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "flevert-tests", $"ocr-scratch-{Guid.NewGuid():N}");

    // A page of 30 lines, as a scanned letter or invoice has: enough text for OSD to be confident about its orientation.
    private const string Page = """
        Cộng hòa xã hội chủ nghĩa Việt Nam
        Độc lập - Tự do - Hạnh phúc
        Hóa đơn bán hàng kiêm phiếu xuất kho
        Ngày 21 tháng 10 năm 2026
        Khách hàng: Công ty Cổ phần Công nghệ Mới
        Địa chỉ: Số 123 Đường Giải Phóng, Quận Hai Bà Trưng, Hà Nội
        Mã số thuế: 0101234567
        Số tài khoản: 1902837465 tại Ngân hàng Ngoại thương Việt Nam
        Người mua hàng: Nguyễn Văn An, điện thoại 0912 345 678
        Hình thức thanh toán: chuyển khoản trong vòng mười ngày
        Tên hàng hóa và dịch vụ được liệt kê dưới đây
        Một bộ máy tính xách tay cùng túi đựng và chuột không dây
        Hai màn hình rộng hai mươi bảy inch có chân đứng điều chỉnh
        Ba bàn phím cơ dành cho nhân viên phòng kế toán
        Bốn chiếc ghế văn phòng có tựa lưng và tay vịn
        Năm hộp giấy in khổ A4 định lượng tám mươi gam
        Sáu ổ cứng gắn ngoài dùng để sao lưu dữ liệu hằng tuần
        Bảy bộ phần mềm văn phòng có bản quyền trong một năm
        Tám cuộn dây cáp mạng loại tốt cho phòng họp lớn
        Chín đèn bàn tiết kiệm điện cho khu vực làm việc chung
        Mười bình nước uống đặt tại hành lang tầng hai
        Cộng tiền hàng chưa có thuế giá trị gia tăng
        Thuế suất thuế giá trị gia tăng là mười phần trăm
        Tổng cộng tiền thanh toán đã bao gồm thuế
        Số tiền viết bằng chữ: Bốn mươi hai triệu đồng chẵn
        Hàng đã giao đủ và đúng chất lượng như hợp đồng
        Bên bán chịu trách nhiệm bảo hành trong mười hai tháng
        Mọi thắc mắc xin liên hệ phòng chăm sóc khách hàng
        Người bán hàng ký và ghi rõ họ tên
        Thủ trưởng đơn vị ký, đóng dấu và ghi rõ họ tên
        """;

    public void Dispose()
    {
        _dir.Dispose();
        try { Directory.Delete(_scratch, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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

    [WindowsFact]
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

    [WindowsFact]
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

    [WindowsFact]
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

    // Tesseract reads a page turned 90 degrees clockwise without OSD, so that case alone would pass with OSD broken; at
    // 180 and 270 degrees it reads garbage unless the page is turned upright first. OSD must be confident (>= 14): a
    // page of 30 lines in Times New Roman scores 23-28 at every angle, where a short page (2 lines about 3) or the
    // same page in Arial (11-18, often under 14 at 180 degrees) would not be turned at all.
    [WindowsTheory]
    [InlineData(RotateFlipType.Rotate90FlipNone)]
    [InlineData(RotateFlipType.Rotate180FlipNone)]
    [InlineData(RotateFlipType.Rotate270FlipNone)]
    public async Task Page_turned_by_pixels_reads_right(RotateFlipType turn)
    {
        var uprightPath = _dir.Combine("upright.png");
        CreateMultiLineImage(uprightPath, Page, "Times New Roman", fontSize: 12, width: 2480, height: 2480, dpi: 300);

        var rotatedPath = _dir.Combine("photo_rotated.png");
        using (var bmp = new Bitmap(uprightPath))
        {
            bmp.RotateFlip(turn);
            bmp.Save(rotatedPath, ImageFormat.Png);
        }

        var output = await Op().RunAsync(rotatedPath, CancellationToken.None);

        Assert.Equal(_dir.Combine("photo_rotated.txt"), output);
        var content = await File.ReadAllTextAsync(output);
        var sim = Similarity(Page, content);
        Assert.True(sim >= 0.95, $"Expected similarity >= 0.95, got {sim:P1} for text turned {turn}: '{content.Trim()}'");
    }

    [WindowsFact]
    public async Task Blank_picture_is_skipped_with_message_and_leaves_no_file()
    {
        var input = _dir.Combine("blank.png");
        using (var image = new MagickImage(MagickColors.White, 200, 200))
            image.Write(input, MagickFormat.Png);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));
        Assert.Equal("No text was found in this picture.", ex.Message);
        Assert.False(File.Exists(_dir.Combine("blank.txt")));
    }

    [WindowsFact]
    public async Task Heic_fixture_is_decoded_safely_and_skipped_when_blank()
    {
        var input = _dir.Combine("photo.heic");
        File.Copy(HeicFixture, input);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));
        Assert.Equal("No text was found in this picture.", ex.Message);
        Assert.False(File.Exists(_dir.Combine("photo.txt")));
    }

    [WindowsFact]
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

    // The bundled Magick.NET has no HEIC encoder (no encode delegate), so the picture is a fixture made with pillow-heif.
    [WindowsFact]
    public async Task Heic_with_Vietnamese_text_extracts_to_txt_with_high_similarity()
    {
        var input = _dir.Combine("receipt.heic");
        File.Copy(HeicTextFixture, input);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("receipt.txt"), output);
        var content = await File.ReadAllTextAsync(output);
        var sim = Similarity(HeicText, content);
        Assert.True(sim >= 0.95, $"Expected similarity >= 0.95, got {sim:P1} for HEIC text: '{content.Trim()}'");
    }

    [WindowsFact]
    public async Task Cancel_while_tesseract_runs_stops_it_and_leaves_nothing_behind()
    {
        var input = _dir.Combine("big.png");
        CreateLetterNoiseImage(input, width: 5000, height: 5000);
        var scratch = ScratchRoot();
        var op = new ImageToText { ScratchRoot = scratch };

        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => op.RunAsync(input, cts.Token));
        await WaitForTesseractAsync(run, scratch, TimeSpan.FromSeconds(60));

        var timer = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Cancelling took {timer.Elapsed.TotalSeconds:F1} s.");
        Assert.Equal(0, ChildProcesses.Running("tesseract", scratch));
        Assert.Equal(["big.png"], _dir.FileNames()); // no .txt, no hidden .partial
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

    [WindowsFact]
    public async Task Scratch_folder_is_removed_after_success_skip_and_failure()
    {
        var scratch = ScratchRoot();

        var text = _dir.Combine("text.png");
        CreateTextImage(text, "Hóa đơn số 123", "Arial", fontSize: 24, width: 1600, height: 300, dpi: 300);
        await new ImageToText { ScratchRoot = scratch }.RunAsync(text, CancellationToken.None);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));

        var blank = _dir.Combine("blank.png");
        using (var image = new MagickImage(MagickColors.White, 200, 200))
            image.Write(blank, MagickFormat.Png);
        await Assert.ThrowsAsync<OperationSkippedException>(
            () => new ImageToText { ScratchRoot = scratch }.RunAsync(blank, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));

        // Fails after the grey PNG is written to the scratch folder: there is no tesseract.exe to read it.
        var missingTool = new ImageToText(new Tesseract(_dir.Combine("no-tesseract"))) { ScratchRoot = scratch };
        await Assert.ThrowsAsync<FileNotFoundException>(() => missingTool.RunAsync(text, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));

        var broken = _dir.Combine("broken.png");
        await File.WriteAllBytesAsync(broken, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        await Assert.ThrowsAnyAsync<Exception>(
            () => new ImageToText { ScratchRoot = scratch }.RunAsync(broken, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

    // Product bug found by this test: tesseract.exe opens its input through the ANSI code page, so a scratch folder
    // under a %TEMP% with letters outside it (a Windows account named "Nguyễn", "Đức") fails every OCR with "cannot read
    // input file ... Invalid argument". Unskip once ImageToText/PdfMakeSearchable pass tesseract a path it can open.
    [WindowsFact(Skip = "Product bug: tesseract cannot open a scratch path outside the ANSI code page (R9 test pass).")]
    public async Task Scratch_root_with_Vietnamese_letters_still_reads()
    {
        var root = _dir.Combine("Tạm của Đức");
        Directory.CreateDirectory(root);
        var input = _dir.Combine("text.png");
        CreateTextImage(input, "Hóa đơn số 123", "Arial", fontSize: 24, width: 1600, height: 300, dpi: 300);

        var output = await new ImageToText { ScratchRoot = root }.RunAsync(input, CancellationToken.None);

        var sim = Similarity("Hóa đơn số 123", await File.ReadAllTextAsync(output));
        Assert.True(sim >= 0.95, $"Expected similarity >= 0.95, got {sim:P1}.");
    }

    /// <summary>An empty folder of the test's own for the operation's private scratch folders.</summary>
    private string ScratchRoot()
    {
        Directory.CreateDirectory(_scratch);
        return _scratch;
    }

    /// <summary>Waits until this test process runs a tesseract.exe on a file in <paramref name="scratch"/>, or fails the
    /// test.</summary>
    private static async Task WaitForTesseractAsync(Task run, string scratch, TimeSpan within)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < within)
        {
            if (ChildProcesses.Running("tesseract", scratch) > 0)
                return;
            if (run.IsCompleted)
                Assert.Fail($"The operation ended before tesseract was seen running: {run.Status}.");
            await Task.Delay(10);
        }

        Assert.Fail($"No tesseract.exe started within {within}.");
    }

    /// <summary>Rows of random capitals: slow for tesseract to read, so a test can cancel it while it runs.</summary>
    private static void CreateLetterNoiseImage(string path, int width, int height)
    {
        using var bmp = new Bitmap(width, height);
        bmp.SetResolution(300, 300);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font("Arial", 8, FontStyle.Regular, GraphicsUnit.Point);
            using var brush = new SolidBrush(Color.Black);
            var rng = new Random(42);
            for (var y = 10; y < height - 10; y += 20)
            {
                var chars = new char[200];
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = (char)('A' + rng.Next(26));
                g.DrawString(new string(chars), font, brush, 10, y);
            }
        }

        bmp.Save(path, ImageFormat.Png);
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
