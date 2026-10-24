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

        var uprightImg = _dir.Combine("upright.png");
        TestHelpers.CreateMultiLineImage(uprightImg, paragraph, "Arial", fontSize: 20);

        var uprightRotation = await Tesseract.NextToApp.DetectRotationAsync(uprightImg, CancellationToken.None);
        Assert.Equal(0, uprightRotation);

        var rotatedImg = _dir.Combine("rotated90.png");
        TestHelpers.CreateRotatedImage(uprightImg, rotatedImg, RotateFlipType.Rotate90FlipNone);

        var rotatedRotation = await Tesseract.NextToApp.DetectRotationAsync(rotatedImg, CancellationToken.None);
        Assert.NotEqual(0, rotatedRotation);
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
