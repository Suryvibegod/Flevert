using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

public sealed class PdfCompressTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op() => PdfOperations.All.OfType<IOperation>().Single(op => op.Id == "pdf.compress");

    private string CopyFixture(string fixture, string name)
    {
        var path = _dir.Combine(name);
        File.Copy(Fixture(fixture), path);
        return path;
    }

    [Fact]
    public void It_is_named_and_placed_as_in_the_spec()
    {
        var op = Op();

        Assert.Equal("Compress/Lossless", op.MenuPath);
        Assert.Equal([".pdf"], op.Extensions);
        Assert.Equal(4, op.MaxParallel);
    }

    [Fact]
    public async Task A_pdf_with_uncompressed_content_gets_much_smaller_and_looks_the_same()
    {
        var input = _dir.Combine("scan.pdf");
        WriteBloated(input, 20_000);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length / 3,
            $"{new FileInfo(input).Length} → {new FileInfo(output).Length} bytes");
        AssertLooksTheSame(input, output);
        await AssertValid(output);
    }

    [Fact]
    public async Task Content_compressed_at_the_fastest_level_is_compressed_again_at_the_best()
    {
        // qpdf keeps a Flate stream as it is unless asked to recompress it, so only --recompress-flate with level 9 makes
        // this smaller; object streams alone save a few bytes of a file this size.
        var input = _dir.Combine("fast.pdf");
        WriteBloated(input, 20_000, CompressionLevel.Fastest);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length * 0.9,
            $"{new FileInfo(input).Length} → {new FileInfo(output).Length} bytes");
        AssertLooksTheSame(input, output);
        // Level 9, not qpdf's default (zlib's 6): measured on this content, 59 906 against 62 450 bytes.
        var atDefault = _dir.Combine("default level.pdf");
        var run = await ExternalTool.RunAsync(Qpdf.NextToApp.ExePath,
            ["--object-streams=generate", "--recompress-flate", input, atDefault], TimeSpan.FromSeconds(30));
        Assert.Equal(0, run.ExitCode);
        Assert.True(new FileInfo(output).Length < new FileInfo(atDefault).Length,
            $"{new FileInfo(output).Length} bytes, {new FileInfo(atDefault).Length} at qpdf's default level");
    }

    [Fact]
    public async Task The_objects_go_into_object_streams()
    {
        var input = CopyFixture("metadata.pdf", "report.pdf");

        var output = await Op().RunAsync(input, CancellationToken.None);

        // An object stream's dictionary stays readable: only its content is compressed.
        Assert.Contains("/ObjStm", Encoding.Latin1.GetString(File.ReadAllBytes(output)), StringComparison.Ordinal);
        Assert.DoesNotContain("/ObjStm", Encoding.Latin1.GetString(File.ReadAllBytes(input)), StringComparison.Ordinal);
        AssertLooksTheSame(input, output);
        await AssertValid(output);
    }

    [Fact]
    public async Task An_already_compact_pdf_is_skipped_and_leaves_nothing()
    {
        var bloated = _dir.Combine("scan.pdf");
        WriteBloated(bloated, 20_000);
        var compact = await Op().RunAsync(bloated, CancellationToken.None);
        var before = FileNames(_dir.Path);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(compact, CancellationToken.None));

        Assert.Equal("This PDF is already compact: compressing it would not make it smaller.", ex.Message);
        Assert.Equal(before, FileNames(_dir.Path));
        Assert.Empty(Directory.GetDirectories(_dir.Path));
    }

    [Fact]
    public async Task The_runner_reports_an_already_compact_pdf_as_skipped_not_failed()
    {
        using var logs = new TempDir();
        var bloated = _dir.Combine("scan.pdf");
        WriteBloated(bloated, 20_000);
        var compact = await Op().RunAsync(bloated, CancellationToken.None);

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [compact], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Skipped);
        Assert.Null(result.Output);
        Assert.Contains("already compact", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_output_is_named_compressed_beside_the_original_and_never_replaces_a_file()
    {
        var input = _dir.Combine("invoice.pdf");
        WriteBloated(input, 5_000);

        var first = await Op().RunAsync(input, CancellationToken.None);
        var second = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("invoice (compressed).pdf"), first);
        Assert.Equal(_dir.Combine("invoice (compressed) (1).pdf"), second);
        Assert.Equal(["invoice (compressed) (1).pdf", "invoice (compressed).pdf", "invoice.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task The_original_is_left_byte_for_byte_unchanged()
    {
        var input = _dir.Combine("scan.pdf");
        WriteBloated(input, 5_000);
        var hash = SHA256.HashData(File.ReadAllBytes(input));
        var written = File.GetLastWriteTimeUtc(input);

        await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(input)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(input));
    }

    [Fact]
    public async Task A_password_protected_pdf_fails_with_the_password_message_and_leaves_nothing()
    {
        var input = CopyFixture("password-protected.pdf", "locked.pdf");

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.PasswordRequired, ex.Error);
        Assert.Equal(PdfException.NeedsPasswordMessage, ex.Message);
        Assert.Equal(["locked.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task A_file_that_is_not_a_pdf_fails_cleanly_and_leaves_nothing()
    {
        var input = _dir.File("notes.pdf", "This is a text file someone renamed to .pdf.\r\n");

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.NotPdf, ex.Error);
        Assert.Equal(["notes.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task Works_on_a_pdf_at_a_long_path_whose_names_are_not_ascii()
    {
        var folder = @"\\?\" + _dir.Path;
        while (folder.Length < 320)
            folder = Path.Combine(folder, "thư mục con rất dài để vượt quá giới hạn MAX_PATH \U0001F34A");
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "Hóa đơn — 中文 \U0001F9FE.pdf");
        WriteBloated(input, 5_000);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(Path.Combine(folder, "Hóa đơn — 中文 \U0001F9FE (compressed).pdf"), output);
        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length);
    }

    [Fact]
    public async Task A_pdf_that_takes_too_long_to_decode_is_compressed_without_decoding_and_says_so()
    {
        // 20 000 small objects that object streams pack, and 10 KB of Flate inside Flate that inflates to 4 GB (15 to 20 s
        // to decode and compress again, T18): the full run stops after 3 s, and the second copies every stream as it is.
        var input = _dir.Combine("bomb.pdf");
        MaliciousPdf.WriteManyObjectsAndBomb(input, 20_000);
        using var logs = new TempDir();
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [input], null, CancellationToken.None);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}");
        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Flevert compressed this PDF only in part: compressing it fully took too long.", result.Note);
        Assert.True(new FileInfo(result.Output!).Length < new FileInfo(input).Length * 3 / 4,
            $"{new FileInfo(input).Length} → {new FileInfo(result.Output!).Length} bytes");
        // The bomb is copied as it was: still Flate inside Flate. A stream's dictionary is never in an object stream, so
        // it is in the file as text.
        var text = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(result.Output!));
        Assert.Matches(@"/Filter\s*\[\s*/FlateDecode\s*/FlateDecode\s*\]", text);
    }

    [Fact]
    public void The_full_run_gets_three_seconds_plus_one_per_megabyte()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), PdfCompress.FullRunTimeoutFor(0));
        Assert.Equal(TimeSpan.FromSeconds(103), PdfCompress.FullRunTimeoutFor(100L << 20));
    }

    [Fact]
    public async Task A_pdf_of_more_than_2000_pages_is_refused_before_anything_is_written()
    {
        var input = MaliciousPdf.Write("count-lie-low", _dir.Combine("many.pdf"));

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.TooManyPages, ex.Error);
        Assert.Equal(["many.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task Cancelling_stops_qpdf_and_leaves_no_partial_file()
    {
        var input = _dir.Combine("big.pdf");
        WriteBlank(input, 200_000, resources: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op().RunAsync(input, cancel.Token));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
        Assert.Equal(["big.pdf"], FileNames(_dir.Path));
    }

    private static void AssertLooksTheSame(string input, string output)
    {
        using var before = PdfDocument.Open(input);
        using var after = PdfDocument.Open(output);
        Assert.Equal(before.PageCount, after.PageCount);
        for (var page = 0; page < before.PageCount; page++)
            Assert.Equal(before.Render(page, 72).Bgra.ToArray(), after.Render(page, 72).Bgra.ToArray());
    }

    private static async Task AssertValid(string path)
    {
        var check = await ExternalTool.RunAsync(Qpdf.NextToApp.ExePath, ["--check", path], TimeSpan.FromSeconds(30));
        Assert.True(check.ExitCode == 0, check.StandardError + check.StandardOutput);
    }
}
