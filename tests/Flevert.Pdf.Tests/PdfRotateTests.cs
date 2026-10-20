using System.Security.Cryptography;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

public sealed class PdfRotateTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string id) => PdfOperations.All.Single(op => op.Id == id);

    private string CopyFixture(string fixture, string name)
    {
        var path = _dir.Combine(name);
        File.Copy(Fixture(fixture), path);
        return path;
    }

    [Fact]
    public void The_rotations_fixture_shows_each_mark_where_its_readme_says()
    {
        using var pdf = PdfDocument.Open(Fixture("rotations.pdf"));

        Assert.Equal(
            [Corner.TopLeft, Corner.TopRight, Corner.BottomRight, Corner.BottomLeft],
            Enumerable.Range(0, pdf.PageCount).Select(page => MarkCorner(pdf, page)));
    }

    private static IOperation[] Rotations() => [.. PdfOperations.All.OfType<PdfRotate>()];

    [Fact]
    public void The_three_rotations_are_named_and_placed_as_in_the_spec()
    {
        Assert.Equal(
            [("pdf.rotate-right", "Rotate/90° right"), ("pdf.rotate-left", "Rotate/90° left"), ("pdf.rotate-180", "Rotate/180°")],
            Rotations().Select(op => (op.Id, op.MenuPath)));
        foreach (var op in Rotations())
        {
            Assert.Equal([".pdf"], op.Extensions);
            // qpdf runs as its own single-threaded process, one per file.
            Assert.Equal(4, op.MaxParallel);
        }
    }

    [Theory]
    [InlineData("pdf.rotate-right", 1)]
    [InlineData("pdf.rotate-180", 2)]
    [InlineData("pdf.rotate-left", 3)]
    public async Task Every_page_turns_on_screen_by_the_angle_from_where_it_was(string id, int quartersClockwise)
    {
        var input = CopyFixture("rotations.pdf", "scan.pdf");

        var output = await Op(id).RunAsync(input, CancellationToken.None);

        using var before = PdfDocument.Open(input);
        using var after = PdfDocument.Open(output);
        Assert.Equal(before.PageCount, after.PageCount);
        for (var page = 0; page < before.PageCount; page++)
        {
            Assert.Equal((Corner)(((int)MarkCorner(before, page) + quartersClockwise) % 4), MarkCorner(after, page));
            var (was, now) = (before.GetPageSize(page), after.GetPageSize(page));
            Assert.Equal(quartersClockwise == 2 ? was : new PdfPageSize(was.Height, was.Width), now);
        }
    }

    [Theory]
    [InlineData("pdf.rotate-right", 90)]
    [InlineData("pdf.rotate-180", 180)]
    [InlineData("pdf.rotate-left", 270)]
    public async Task Every_page_gets_its_old_rotate_value_plus_the_angle(string id, int degrees)
    {
        var input = CopyFixture("rotations.pdf", "scan.pdf");

        var output = await Op(id).RunAsync(input, CancellationToken.None);

        var before = await ReadRotations(input);
        Assert.Equal([0, 90, 180, 270], before);
        Assert.Equal(before.Select(r => (r + degrees) % 360), await ReadRotations(output));
    }

    [Fact]
    public async Task The_output_is_named_rotated_beside_the_original_and_never_replaces_a_file()
    {
        var input = CopyFixture("three-colours.pdf", "invoice.pdf");

        var first = await Op("pdf.rotate-right").RunAsync(input, CancellationToken.None);
        var second = await Op("pdf.rotate-right").RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("invoice (rotated).pdf"), first);
        Assert.Equal(_dir.Combine("invoice (rotated) (1).pdf"), second);
        Assert.Equal(["invoice (rotated) (1).pdf", "invoice (rotated).pdf", "invoice.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task The_original_is_left_byte_for_byte_unchanged()
    {
        var input = CopyFixture("rotations.pdf", "scan.pdf");
        var hash = SHA256.HashData(File.ReadAllBytes(input));
        var written = File.GetLastWriteTimeUtc(input);

        await Op("pdf.rotate-180").RunAsync(input, CancellationToken.None);

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(input)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(input));
    }

    [Fact]
    public async Task A_password_protected_pdf_fails_with_the_password_message_and_leaves_nothing()
    {
        var input = CopyFixture("password-protected.pdf", "locked.pdf");

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op("pdf.rotate-left").RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.PasswordRequired, ex.Error);
        Assert.Equal(PdfException.NeedsPasswordMessage, ex.Message);
        Assert.Equal(["locked.pdf"], FileNames(_dir.Path));
    }

    [Theory]
    [InlineData("This is a text file someone renamed to .pdf.\r\n")]
    [InlineData("")]
    [InlineData("%PDF-1.7\n1 0 obj\n<< /Type /Catalog")]
    public async Task A_file_that_is_not_a_pdf_fails_cleanly_and_leaves_no_partial_file(string content)
    {
        var input = _dir.File("notes.pdf", content);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op("pdf.rotate-right").RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.NotPdf, ex.Error);
        Assert.Equal("This file is not a PDF, or it is damaged.", ex.Message);
        Assert.Equal(["notes.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task A_pdf_qpdf_repairs_with_warnings_is_still_rotated()
    {
        // Pages without /Resources: every viewer opens it, qpdf adds the entry and exits with code 3 (warnings).
        var input = _dir.Combine("no resources.pdf");
        WriteBlank(input, 3);

        var output = await Op("pdf.rotate-right").RunAsync(input, CancellationToken.None);

        var rotations = await ReadRotations(output);
        Assert.Equal([90, 90, 90], rotations);
    }

    [Fact]
    public async Task Works_on_a_pdf_whose_folder_and_name_are_not_ascii()
    {
        var input = CopyFixture("rotations.pdf", "Hóa đơn tháng 10 — 中文.pdf");

        var output = await Op("pdf.rotate-right").RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Hóa đơn tháng 10 — 中文 (rotated).pdf"), output);
        var rotations = await ReadRotations(output);
        Assert.Equal([90, 180, 270, 0], rotations);
    }

    [Fact]
    public async Task A_name_with_a_lone_surrogate_is_refused_rather_than_opening_another_file()
    {
        // NTFS names are UTF-16 and may hold a lone surrogate, which UTF-8 cannot carry. Replaced by U+FFFD, the path
        // qpdf reads would name another file: here a sibling that exists.
        var input = CopyFixture("rotations.pdf", "scan\uD800.pdf");
        CopyFixture("three-colours.pdf", "scan�.pdf");

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op("pdf.rotate-right").RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.UnsupportedFileName, ex.Error);
        Assert.Contains("rename", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["scan\uD800.pdf", "scan�.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task Works_on_a_pdf_at_a_long_path_given_with_the_extended_prefix()
    {
        var folder = @"\\?\" + _dir.Path;
        while (folder.Length < 320)
            folder = Path.Combine(folder, "thư mục con rất dài để vượt quá giới hạn MAX_PATH");
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "Ảnh Đà Lạt.pdf");
        File.Copy(Fixture("rotations.pdf"), input);

        var output = await Op("pdf.rotate-left").RunAsync(input, CancellationToken.None);

        Assert.Equal(Path.Combine(folder, "Ảnh Đà Lạt (rotated).pdf"), output);
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(Corner.BottomLeft, MarkCorner(pdf, 0));
    }

    [Fact]
    public async Task Works_through_the_runner_on_a_path_just_under_260_characters()
    {
        // The runner gives an operation the plain form of a path under 260 characters (InputPin.Shorten), and the
        // temporary output beside it is 51 characters longer; past 260, qpdf opens a path only with \\?\.
        using var logs = new TempDir();
        var folder = LongPath(_dir.Path);
        while (folder.Length < 230)
            folder = Path.Combine(folder, "thư mục");
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, new string('a', 250 - folder.Length - ".pdf".Length - 1) + ".pdf");
        File.Copy(Fixture("rotations.pdf"), input);
        Assert.Equal(250, input.Length);

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op("pdf.rotate-right"), [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Path.Combine(folder, Path.GetFileNameWithoutExtension(input) + " (rotated).pdf"), result.Output);
        using var pdf = PdfDocument.Open(result.Output!);
        Assert.Equal(Corner.TopRight, MarkCorner(pdf, 0));
    }

    [Fact]
    public async Task Cancelling_stops_qpdf_and_leaves_no_partial_file()
    {
        // About 5 s of qpdf work: long enough to cancel in the middle.
        var input = _dir.Combine("big.pdf");
        WriteBlank(input, 200_000, resources: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op("pdf.rotate-right").RunAsync(input, cancel.Token));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
        Assert.Equal(["big.pdf"], FileNames(_dir.Path));
    }
}
