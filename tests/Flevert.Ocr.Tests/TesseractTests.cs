using System.Diagnostics;
using System.Drawing;
using System.Text;
using Flevert.Core;
using Xunit.Abstractions;

namespace Flevert.Ocr.Tests;

public sealed class TesseractTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [WindowsFact]
    public async Task Recognises_Vietnamese_text_with_high_similarity()
    {
        const string text1 = "Cộng hòa xã hội chủ nghĩa Việt Nam";
        var img1 = _dir.Combine("sample1.png");
        TestHelpers.CreateTextImage(img1, text1, "Arial", fontSize: 22);

        var result1 = await Tesseract.NextToApp.ReadTextAsync(img1, CancellationToken.None);
        var sim1 = TestHelpers.Similarity(text1, result1);
        Assert.True(sim1 >= 0.95, $"Expected similarity >= 0.95, got {sim1:P1} for text: '{result1.Trim()}'");

        const string text2 = "Hóa đơn số 123";
        var img2 = _dir.Combine("sample2.png");
        TestHelpers.CreateTextImage(img2, text2, "Times New Roman", fontSize: 24);

        var result2 = await Tesseract.NextToApp.ReadTextAsync(img2, CancellationToken.None);
        var sim2 = TestHelpers.Similarity(text2, result2);
        Assert.True(sim2 >= 0.95, $"Expected similarity >= 0.95, got {sim2:P1} for text: '{result2.Trim()}'");
    }

    // A Vietnamese user's profile (C:\Users\Dương\) holds %TEMP% and the per-user install folder, so tesseract.exe, its
    // tessdata and every page image Flevert hands it sit under a name the ANSI code page cannot spell. The binary
    // reads its arguments and TESSDATA_PREFIX as UTF-8 only because its manifest sets activeCodePage (ADR-0010).
    [WindowsFact]
    public async Task Reads_image_and_tessdata_from_a_folder_with_Vietnamese_letters()
    {
        var folder = _dir.Combine("Cộng hòa Dương");
        CopyTree(Path.GetDirectoryName(Tesseract.NextToApp.ExecutablePath)!, folder);
        var engine = new Tesseract(folder);

        const string text = "Hóa đơn số 123";
        var image = Path.Combine(folder, "ảnh trang 1.png");
        TestHelpers.CreateTextImage(image, text, "Arial", fontSize: 24);

        var result = await engine.ReadTextAsync(image, CancellationToken.None);
        var similarity = TestHelpers.Similarity(text, result);
        Assert.True(similarity >= 0.95, $"Expected similarity >= 0.95, got {similarity:P1} for text: '{result.Trim()}'");

        var layer = Path.Combine(folder, "lớp chữ");
        await engine.WriteTextLayerAsync(image, layer, CancellationToken.None);
        Assert.True(File.Exists(layer + ".pdf"), $"Expected PDF file at {layer}.pdf");
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    [WindowsFact]
    public async Task Writes_text_layer_pdf_with_glyphless_font()
    {
        var img = _dir.Combine("invoice.png");
        TestHelpers.CreateTextImage(img, "Hóa đơn số 123", "Arial", fontSize: 22);

        var outputBase = _dir.Combine("invoice_layer");
        var expectedPdf = outputBase + ".pdf";

        var text = await Tesseract.NextToApp.WriteTextLayerAsync(img, outputBase, CancellationToken.None);

        Assert.Contains("123", text, StringComparison.Ordinal);
        Assert.True(File.Exists(expectedPdf), $"Expected PDF file at {expectedPdf}");
        var bytes = await File.ReadAllBytesAsync(expectedPdf);
        Assert.True(bytes.Length > 100, "PDF file is too small.");

        var header = Encoding.ASCII.GetString(bytes, 0, 4);
        Assert.Equal("%PDF", header);

        var rawString = Encoding.Latin1.GetString(bytes);
        Assert.Contains("GlyphLessFont", rawString);
    }

    [WindowsFact]
    public async Task Detects_rotation_on_upright_and_rotated_images()
    {
        // DetectRotationAsync turns a page only when OSD is confident (>= 14): a short page (2 lines score about 3) or
        // the same 30 lines in Arial (11-18, often under 14) would report 0 at any angle; in Times New Roman they score
        // 23-28 at every angle.
        const string page = """
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

        var uprightImg = _dir.Combine("upright.png");
        TestHelpers.CreateMultiLineImage(uprightImg, page, "Times New Roman", fontSize: 12, width: 2480, height: 2480);

        Assert.Equal(0, await Tesseract.NextToApp.DetectRotationAsync(uprightImg, CancellationToken.None));

        // The page turned clockwise by the pixels, and how far clockwise it must turn back to stand upright.
        (RotateFlipType Turn, int Expected)[] cases =
        [
            (RotateFlipType.Rotate90FlipNone, 270),
            (RotateFlipType.Rotate180FlipNone, 180),
            (RotateFlipType.Rotate270FlipNone, 90),
        ];
        foreach (var (turn, expected) in cases)
        {
            var rotatedImg = _dir.Combine($"{turn}.png");
            TestHelpers.CreateRotatedImage(uprightImg, rotatedImg, turn);

            var rotation = await Tesseract.NextToApp.DetectRotationAsync(rotatedImg, CancellationToken.None);
            Assert.True(expected == rotation, $"{turn}: expected {expected}, got {rotation}.");
        }
    }

    [WindowsFact]
    public async Task Cancelling_long_run_returns_within_2_seconds_and_kills_process()
    {
        var bigImg = _dir.Combine("large_noise.png");
        TestHelpers.CreateLargeNoiseImage(bigImg, width: 5000, height: 5000);

        using var cts = new CancellationTokenSource();
        var ocrTask = Task.Run(() => Tesseract.NextToApp.ReadTextAsync(bigImg, cts.Token));

        int? childPid = null;
        var pollTimer = Stopwatch.StartNew();
        while (pollTimer.ElapsedMilliseconds < 5000)
        {
            childPid = ProcessHelper.FindChildTesseractProcessId(Environment.ProcessId);
            if (childPid is not null)
                break;
            await Task.Delay(15);
        }

        Assert.NotNull(childPid);

        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ocrTask);
        sw.Stop();

        Console.WriteLine($"[CancelTiming] Child PID: {childPid.Value}, Duration: {sw.ElapsedMilliseconds} ms");
        output.WriteLine($"[CancelTiming] Child PID: {childPid.Value}, Duration: {sw.ElapsedMilliseconds} ms");

        Assert.True(sw.ElapsedMilliseconds < 2000, $"Cancellation took {sw.ElapsedMilliseconds} ms, expected < 2000 ms.");
        Assert.False(ProcessHelper.IsProcessRunning(childPid.Value), $"Child process {childPid.Value} should have exited.");
    }

    [WindowsFact]
    public async Task Missing_tessdata_folder_throws_clear_error()
    {
        var dummyImg = _dir.Combine("dummy.png");
        TestHelpers.CreateTextImage(dummyImg, "Test", fontSize: 16);

        var nonExistentTessData = _dir.Combine("no_such_tessdata");
        var badEngine = new Tesseract(Tesseract.NextToApp.ExecutablePath, nonExistentTessData);

        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await badEngine.ReadTextAsync(dummyImg, CancellationToken.None);
        });

        Assert.Contains("tessdata folder is missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scripts\\fetch-deps.ps1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task User_TESSDATA_PREFIX_is_ignored()
    {
        var dummyImg = _dir.Combine("dummy_env.png");
        TestHelpers.CreateTextImage(dummyImg, "Hóa đơn số 123", fontSize: 24);

        var bogusDir = _dir.Combine("bogus_user_tessdata");
        Directory.CreateDirectory(bogusDir);

        var previousEnv = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        try
        {
            Environment.SetEnvironmentVariable("TESSDATA_PREFIX", bogusDir);

            var text = await Tesseract.NextToApp.ReadTextAsync(dummyImg, CancellationToken.None);
            Assert.Contains("123", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSDATA_PREFIX", previousEnv);
        }
    }

    [Fact]
    public async Task Memory_limit_exceeded_throws_OcrException_with_clear_message()
    {
        var dummyImg = _dir.Combine("mem.png");
        TestHelpers.CreateTextImage(dummyImg, "Hello", fontSize: 16);

        Task<ExternalToolResult> FakeRun(
            string path, IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            return Task.FromResult(new ExternalToolResult(1, "", "out of memory", false, false)
            {
                MemoryLimitExceeded = true,
            });
        }

        var engine = new Tesseract(Tesseract.NextToApp.ExecutablePath, Tesseract.NextToApp.TessDataPath, FakeRun);

        var ex = await Assert.ThrowsAsync<OcrException>(async () =>
        {
            await engine.ReadTextAsync(dummyImg, CancellationToken.None);
        });

        Assert.Equal(OcrException.NeedsTooMuchMemoryMessage, ex.Message);
        Assert.IsType<TesseractException>(ex.InnerException);
        Assert.Contains("out of memory", ex.InnerException.Message);
    }

    [Fact]
    public async Task Process_failure_throws_OcrException_with_stderr_in_inner_exception()
    {
        var dummyImg = _dir.Combine("fail.png");
        TestHelpers.CreateTextImage(dummyImg, "Hello", fontSize: 16);

        Task<ExternalToolResult> FakeRun(
            string path, IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            return Task.FromResult(new ExternalToolResult(2, "", "Image resolution too low", false, false));
        }

        var engine = new Tesseract(Tesseract.NextToApp.ExecutablePath, Tesseract.NextToApp.TessDataPath, FakeRun);

        var ex = await Assert.ThrowsAsync<OcrException>(async () =>
        {
            await engine.ReadTextAsync(dummyImg, CancellationToken.None);
        });

        Assert.Equal(OcrException.ToolFailedMessage, ex.Message);
        Assert.IsType<TesseractException>(ex.InnerException);
        Assert.Contains("Image resolution too low", ex.InnerException.Message);
    }

    [Fact]
    public async Task Disk_full_throws_OcrException_with_disk_full_message()
    {
        var dummyImg = _dir.Combine("disk_full.png");
        TestHelpers.CreateTextImage(dummyImg, "Hello", fontSize: 16);

        Task<ExternalToolResult> FakeRun(
            string path, IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            return Task.FromResult(new ExternalToolResult(1, "", "No space left on device", false, false));
        }

        var engine = new Tesseract(Tesseract.NextToApp.ExecutablePath, Tesseract.NextToApp.TessDataPath, FakeRun);

        var ex = await Assert.ThrowsAsync<OcrException>(async () =>
        {
            await engine.ReadTextAsync(dummyImg, CancellationToken.None);
        });

        Assert.Equal(OcrException.DiskFullMessage, ex.Message);
    }

    [WindowsFact]
    public async Task ReadText_on_blank_image_returns_empty_string()
    {
        var blankImg = _dir.Combine("blank.png");
        TestHelpers.CreateTextImage(blankImg, "   ", fontSize: 16);

        var text = await Tesseract.NextToApp.ReadTextAsync(blankImg, CancellationToken.None);
        Assert.True(string.IsNullOrWhiteSpace(text));
    }

    [WindowsFact]
    public async Task DetectRotation_returns_zero_on_blank_image()
    {
        var blankImg = _dir.Combine("blank_rot.png");
        TestHelpers.CreateTextImage(blankImg, "   ", fontSize: 16);

        var rotation = await Tesseract.NextToApp.DetectRotationAsync(blankImg, CancellationToken.None);
        Assert.Equal(0, rotation);
    }

    [WindowsFact]
    public async Task WriteTextLayer_accepts_outputBase_with_pdf_extension()
    {
        var img = _dir.Combine("invoice2.png");
        TestHelpers.CreateTextImage(img, "Hóa đơn số 123", fontSize: 22);

        var outputBaseWithPdf = _dir.Combine("invoice_layer2.pdf");

        await Tesseract.NextToApp.WriteTextLayerAsync(img, outputBaseWithPdf, CancellationToken.None);

        Assert.True(File.Exists(outputBaseWithPdf));
        Assert.False(File.Exists(outputBaseWithPdf + ".pdf"));
    }

    [Fact]
    public async Task Refuses_txt_path_without_starting_tesseract()
    {
        var txtFile = _dir.File("imagelist.txt", "line1\nline2");
        var ran = false;

        Task<ExternalToolResult> FakeRun(
            string path, IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            ran = true;
            return Task.FromResult(new ExternalToolResult(0, "", "", false, false));
        }

        var engine = new Tesseract(Tesseract.NextToApp.ExecutablePath, Tesseract.NextToApp.TessDataPath, FakeRun);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await engine.ReadTextAsync(txtFile, CancellationToken.None);
        });

        Assert.False(ran, "Tesseract must not be started for unapproved file types.");
        Assert.Contains(".txt", ex.Message);
    }
}
