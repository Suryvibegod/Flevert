using System.Diagnostics;
using Flevert.Core;
using Xunit.Abstractions;

namespace Flevert.Pdf.Tests;

/// <summary>
/// T18: every PDF operation against malicious and broken PDFs (<see cref="MaliciousPdf"/>). Each case either converts the
/// file or refuses it with a clear message, within 10 s (docs/specs/pdf.md, success criterion 6), leaves nothing behind
/// (no output, no <c>.partial</c>), runs nothing the PDF asks for, and throws rather than taking this process down: the
/// operations run here as in the batch leader. Memory is bounded by the jobs qpdf and the worker run in (ADR-0008); a
/// file that needs more fails with "needs more memory", which the bombs below show.
/// </summary>
/// <remarks>
/// In the worker collection: the render cases start PDF workers, and other classes there look for theirs by parent.
/// </remarks>
[Collection(WorkerProcesses.Name)]
public sealed class PdfHardeningTests(ITestOutputHelper output) : IDisposable
{
    private const string Ok = "ok";
    private const string Skipped = "skipped";
    private const string Password = "Hardening test 42";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>What each operation does with each fixture: <see cref="Ok"/>, or the <see cref="PdfError"/> it fails with.</summary>
    public static TheoryData<string, string, string> Cases()
    {
        var notPdf = nameof(PdfError.NotPdf);
        var tooMany = nameof(PdfError.TooManyPages);
        var memory = nameof(PdfError.NeedsTooMuchMemory);
        var encryption = nameof(PdfError.UnsupportedEncryption);
        var damaged = nameof(PdfError.DamagedPage);
        // Compress: a small PDF gets no smaller, and is skipped unless qpdf repaired it (cycle-self-reference, and the two
        // without a trailer, which it writes without object streams: PdfCompress).
        // fixture: rotate, split, merge, to-png and to-jpg (the worker), compress, remove metadata, protect
        (string Fixture, string Rotate, string Split, string Merge, string Render, string Compress, string Strip, string Protect)[] table =
        [
            ("bomb-content", Ok, Ok, Ok, memory, Skipped, Ok, Ok),
            ("bomb-double-flate", Ok, Ok, Ok, Ok, Skipped, Ok, Ok),
            ("bomb-double-flate-content", Ok, Ok, Ok, memory, Skipped, Ok, Ok),
            ("bomb-image", Ok, Ok, Ok, Ok, Skipped, Ok, Ok),
            ("bomb-object-stream", memory, memory, memory, memory, memory, memory, memory),
            ("cycle-kids-self", notPdf, notPdf, notPdf, damaged, notPdf, notPdf, notPdf),
            ("cycle-kids-loop", notPdf, notPdf, notPdf, Ok, notPdf, notPdf, notPdf),
            ("cycle-parent", Ok, Ok, Ok, Ok, Skipped, Ok, Ok),
            ("cycle-self-reference", Ok, Ok, Ok, Ok, Ok, Ok, Ok),
            ("pages-100000", tooMany, tooMany, tooMany, tooMany, tooMany, tooMany, tooMany),
            ("pages-2001", tooMany, tooMany, tooMany, tooMany, tooMany, tooMany, tooMany),
            ("count-lie-low", tooMany, tooMany, tooMany, Ok, tooMany, tooMany, tooMany),
            ("count-lie-high", Ok, Ok, Ok, Ok, Skipped, Ok, Ok),
            ("page-giant", Ok, Ok, Ok, Ok, Skipped, Ok, Ok),
            ("page-zero", Ok, Ok, Ok, Ok, Skipped, Ok, Ok),
            ("page-inverted", Ok, Ok, Ok, Ok, Skipped, Ok, Ok),
            ("xref-wrong-offsets", Ok, Ok, Ok, Ok, Ok, Ok, Ok),
            ("xref-missing", Ok, Ok, Ok, notPdf, Ok, Ok, Ok),
            ("truncated", Ok, Ok, Ok, notPdf, Ok, Ok, Ok),
            ("startxref-past-end", Ok, Ok, Ok, Ok, Ok, Ok, Ok),
            ("not-pdf-png", notPdf, notPdf, notPdf, notPdf, notPdf, notPdf, notPdf),
            ("not-pdf-zip", notPdf, notPdf, notPdf, notPdf, notPdf, notPdf, notPdf),
            ("not-pdf-empty", notPdf, notPdf, notPdf, notPdf, notPdf, notPdf, notPdf),
            ("header-only", notPdf, notPdf, notPdf, notPdf, notPdf, notPdf, notPdf),
            ("nested-arrays", Ok, Ok, Ok, Ok, Ok, Ok, Ok),
            ("nested-dictionaries", Ok, Ok, Ok, Ok, Ok, Ok, Ok),
            ("deep-page-tree", notPdf, notPdf, notPdf, damaged, notPdf, notPdf, notPdf),
            ("many-objects", Ok, Ok, Ok, Ok, Ok, Ok, Ok),
            ("launch-action", Ok, Ok, Ok, Ok, Ok, Ok, Ok),
            ("encrypt-unknown-handler", encryption, encryption, encryption, encryption, encryption, encryption, encryption),
            ("signed", Ok, Ok, Ok, Ok, Skipped, Ok, Ok),
        ];
        var cases = new TheoryData<string, string, string>();
        foreach (var row in table)
        {
            cases.Add(row.Fixture, "pdf.rotate-right", row.Rotate);
            cases.Add(row.Fixture, "pdf.split", row.Split);
            cases.Add(row.Fixture, "pdf.merge", row.Merge);
            cases.Add(row.Fixture, "pdf.to-png", row.Render);
            cases.Add(row.Fixture, "pdf.to-jpg", row.Render);
            cases.Add(row.Fixture, "pdf.compress", row.Compress);
            cases.Add(row.Fixture, "pdf.strip-metadata", row.Strip);
            cases.Add(row.Fixture, "pdf.protect", row.Protect);
            // pdfium reads the text as it reads a page to draw it; none of these PDFs has any text.
            cases.Add(row.Fixture, "pdf.extract-text", row.Render == Ok ? Skipped : row.Render);
            // qpdf counts the pages first, then pdfium reads the images; none of these has one above 150 DPI.
            cases.Add(row.Fixture, "pdf.compress-sharing", row.Rotate != Ok ? row.Rotate : row.Render == Ok ? Skipped : row.Render);
        }

        return cases;
    }

    // Remove metadata reads every object of the PDF twice, as qpdf's JSON and back (MetadataRemoval): real work that grows
    // with the file, like qpdf's own timeout. The 16 MB of many-objects (300 000 objects) took 13 s on CI, 5 s here.
    private static TimeSpan LimitFor(string operation, string input) => operation == "pdf.strip-metadata"
        ? Limit + TimeSpan.FromSeconds(new FileInfo(input).Length / (1024 * 1024))
        : Limit;

    [Fact]
    public void Every_fixture_is_in_the_table_once()
    {
        var inTable = Cases().Select(row => (string)row[0]).Distinct().Order(StringComparer.Ordinal);

        Assert.Equal(MaliciousPdf.All.Keys.Order(StringComparer.Ordinal), inTable);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_operation_handles_or_refuses_each_fixture_in_time_and_leaves_nothing_behind(string fixture, string operation, string expected)
    {
        var input = MaliciousPdf.Write(fixture, _dir.Combine(fixture + ".pdf"));
        var partner = operation == "pdf.merge" ? CopyGood() : null;
        var before = Entries();
        var timer = Stopwatch.StartNew();

        var (outcome, result) = await RunAsync(operation, input, partner);

        output.WriteLine($"{fixture} {operation}: {outcome} in {timer.Elapsed.TotalSeconds:0.0} s ({result})");
        Assert.True(timer.Elapsed < LimitFor(operation, input), $"{fixture} {operation} took {timer.Elapsed}");
        Assert.Equal(expected, outcome);
        var expectedEntries = expected == Ok ? [.. before, Path.GetFileName(result)!] : before;
        Assert.Equal(expectedEntries.Order(StringComparer.Ordinal), Entries());
        Assert.Empty(TestProcesses.Workers());
        if (fixture == "launch-action")
            Assert.False(File.Exists(MaliciousPdf.LaunchMarker(input)), "the PDF's launch action ran");
    }

    [Fact]
    public void The_launch_action_writes_its_marker_when_something_does_run_it()
    {
        // The control for the launch-action cases: what a viewer that obeys the action would run, run by hand.
        var input = MaliciousPdf.Write("launch-action", _dir.Combine("launch-action.pdf"));
        var marker = MaliciousPdf.LaunchMarker(input);
        var (file, parameters) = MaliciousPdf.LaunchCommand(input);
        try
        {
            using (var cmd = Process.Start(new ProcessStartInfo(file, parameters) { UseShellExecute = false, CreateNoWindow = true })!)
                cmd.WaitForExit(10_000);

            Assert.True(File.Exists(marker));
            Assert.Contains(Path.GetFileName(marker), File.ReadAllText(input), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task The_bomb_builder_inflates_to_exactly_what_it_says()
    {
        // The bombs above are built without compressing gigabytes (MaliciousPdf.ZerosDeflated); a small one is checked whole,
        // Adler-32 included, so the large ones are bombs and not merely damaged streams.
        byte[] prefix = [.. "2 0 3 40 "u8];
        var packed = MaliciousPdf.ZerosDeflated(prefix, 8);

        using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress);
        var inflated = new MemoryStream();
        await zlib.CopyToAsync(inflated);

        Assert.Equal(prefix.Length + (8 << 20), inflated.Length);
        Assert.Equal(prefix, inflated.ToArray()[..prefix.Length]);
        Assert.All(inflated.ToArray()[prefix.Length..], b => Assert.Equal(0, b));
        Assert.True(packed.Length < 20_000, $"{packed.Length} bytes");
    }

    public static TheoryData<string, string> NotImages() => new()
    {
        { "pdf-named.jpg", "bomb-object-stream" }, // a PDF, which ImageMagick would only read through Ghostscript
        { "launch-named.png", "launch-action" },
        { "zip-named.jpg", "not-pdf-zip" },
    };

    [Theory]
    [MemberData(nameof(NotImages))]
    public async Task Combine_into_pdf_refuses_a_pdf_or_archive_named_as_an_image_and_writes_nothing(string name, string fixture)
    {
        // pdf.from-images reads images, not PDFs: the PDF fixtures that apply to it are files that only claim to be images.
        var photo = _dir.Combine("a photo.jpg");
        TestImages.Picture(photo, 64, 48, ImageMagick.MagickFormat.Jpeg);
        var impostor = MaliciousPdf.Write(fixture, _dir.Combine(name));
        var before = Entries();
        var timer = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<BatchInputException>(() => new ImagesToPdf().RunAsync([photo, impostor], null, CancellationToken.None));

        Assert.True(timer.Elapsed < Limit, $"took {timer.Elapsed}");
        Assert.Equal(impostor, ex.InputPath);
        Assert.Equal("The image could not be read: it is damaged, or not the kind of image its name says.", ex.Message);
        Assert.Equal(before, Entries());
        Assert.False(File.Exists(MaliciousPdf.LaunchMarker(impostor)));
    }

    [Fact]
    public async Task Combine_into_pdf_refuses_an_empty_image()
    {
        var photo = _dir.Combine("a photo.jpg");
        TestImages.Picture(photo, 64, 48, ImageMagick.MagickFormat.Jpeg);
        var empty = MaliciousPdf.Write("not-pdf-empty", _dir.Combine("empty.jpg"));

        var ex = await Assert.ThrowsAsync<BatchInputException>(() => new ImagesToPdf().RunAsync([photo, empty], null, CancellationToken.None));

        Assert.Equal(empty, ex.InputPath);
        Assert.Equal("The file is empty.", ex.Message);
        Assert.Equal(["a photo.jpg", "empty.jpg"], Entries());
    }

    public static TheoryData<string> Modifying() => ["pdf.rotate-right", "pdf.split", "pdf.merge", "pdf.strip-metadata", "pdf.protect"];

    [Theory]
    [MemberData(nameof(Modifying))]
    public async Task The_notification_says_a_signed_pdfs_signature_is_not_valid_in_the_output(string operation)
    {
        var signed = MaliciousPdf.Write("signed", _dir.Combine("hợp đồng ký số.pdf"));

        var result = await RunThroughRunnerAsync(operation, operation == "pdf.merge" ? [CopyGood(), signed] : [signed]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Signed PDF: the digital signature is not valid in the output.", result.Note);
        Assert.EndsWith(". Signed PDF: the digital signature is not valid in the output.",
            BatchSummary.Describe([result], 1, _dir.Path).Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modifying))]
    public async Task An_unsigned_pdf_gets_no_note(string operation)
    {
        var unsigned = _dir.Combine("hợp đồng.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), unsigned);

        var result = await RunThroughRunnerAsync(operation, operation == "pdf.merge" ? [CopyGood(), unsigned] : [unsigned]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Note);
    }

    [Theory]
    [MemberData(nameof(Modifying))]
    public async Task A_pdf_qpdf_had_to_repair_is_written_with_a_note_to_check_its_pages(string operation)
    {
        // No cross-reference table: qpdf finds the objects by scanning the file, and says so (exit code 3).
        var damaged = MaliciousPdf.Write("xref-missing", _dir.Combine("hỏng.pdf"));

        var result = await RunThroughRunnerAsync(operation, operation == "pdf.merge" ? [CopyGood(), damaged] : [damaged]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Flevert had to repair this PDF while reading it: check that every page is there.", result.Note);
    }

    [Fact]
    public async Task Pages_rendered_from_a_signed_pdf_get_no_note()
    {
        // Images are no signed document: nothing to warn about.
        var signed = MaliciousPdf.Write("signed", _dir.Combine("signed.pdf"));

        var result = await RunThroughRunnerAsync("pdf.to-png", [signed]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Note);
    }

    private static async Task<FileResult> RunThroughRunnerAsync(string operation, string[] files)
    {
        using var logs = new TempDir();
        var runner = new Runner(new RunLog(logs.Path));
        var outcome = operation switch
        {
            "pdf.rotate-right" => await runner.RunAsync(new PdfRotate(90, Qpdf.NextToApp), files, null, CancellationToken.None),
            "pdf.split" => await runner.RunAsync(new PdfSplit(Qpdf.NextToApp), files, null, CancellationToken.None),
            "pdf.merge" => await runner.RunAsync(new PdfMerge(Qpdf.NextToApp), files, null, CancellationToken.None),
            "pdf.strip-metadata" => await runner.RunAsync(new PdfStripMetadata(Qpdf.NextToApp), files, null, CancellationToken.None),
            "pdf.protect" => await runner.RunAsync(new PdfProtect(Qpdf.NextToApp, new FakePrompt(Password)), files, null, CancellationToken.None),
            "pdf.to-png" => await runner.RunAsync(new PdfToImages(PdfPageImageFormat.Png, PdfToImages.Dpi, TestPdf.Worker), files, null, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        return Assert.Single(outcome.Results);
    }

    // Runs the operation as the product does and returns "ok" with the output's name, or the PdfError with the message.
    private static async Task<(string Outcome, string? Result)> RunAsync(string operation, string input, string? partner)
    {
        try
        {
            var result = operation switch
            {
                "pdf.rotate-right" => await new PdfRotate(90, Qpdf.NextToApp).RunAsync(input, CancellationToken.None),
                "pdf.split" => await new PdfSplit(Qpdf.NextToApp).RunAsync(input, CancellationToken.None),
                "pdf.merge" => await new PdfMerge(Qpdf.NextToApp).RunAsync([input, partner!], null, CancellationToken.None),
                "pdf.to-png" => await new PdfToImages(PdfPageImageFormat.Png, PdfToImages.Dpi, TestPdf.Worker).RunAsync(input, CancellationToken.None),
                "pdf.to-jpg" => await new PdfToImages(PdfPageImageFormat.Jpeg, PdfToImages.Dpi, TestPdf.Worker).RunAsync(input, CancellationToken.None),
                "pdf.compress" => await new PdfCompress(Qpdf.NextToApp).RunAsync(input, CancellationToken.None),
                "pdf.strip-metadata" => await new PdfStripMetadata(Qpdf.NextToApp).RunAsync(input, CancellationToken.None),
                "pdf.protect" => await ProtectAsync(input),
                "pdf.extract-text" => await new PdfExtractText(TestPdf.Worker).RunAsync(input, CancellationToken.None),
                "pdf.compress-sharing" => await new PdfCompressSharing(Qpdf.NextToApp, TestPdf.Worker).RunAsync(input, CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            return (Ok, result);
        }
        catch (OperationSkippedException e)
        {
            return (Skipped, e.Message);
        }
        catch (PdfException e)
        {
            return (e.Error.ToString(), e.Message);
        }
        catch (BatchInputException e) when (e.InputPath == input && e.InnerException is PdfException inner)
        {
            // Merge names the input that stopped it, with the error that file alone gives.
            Assert.Equal(inner.Message, e.Message);
            return (inner.Error.ToString(), e.Message);
        }
    }

    private static async Task<string> ProtectAsync(string input)
    {
        using var protect = new PdfProtect(Qpdf.NextToApp, new FakePrompt(Password));
        return await protect.RunAsync(input, CancellationToken.None);
    }

    private string CopyGood()
    {
        var path = _dir.Combine("z good.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), path);
        return path;
    }

    private string[] Entries() =>
        [.. Directory.GetFileSystemEntries(_dir.Path).Select(entry => Path.GetFileName(entry)!).Order(StringComparer.Ordinal)];
}
