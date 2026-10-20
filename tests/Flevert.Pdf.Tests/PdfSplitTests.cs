using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

public sealed class PdfSplitTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PdfSplit Op() => (PdfSplit)MenuOperations.All.Single(op => op.Id == "pdf.split");

    private string Pdf(string name, params int[] widths)
    {
        var path = _dir.Combine(name);
        WriteWidths(path, widths);
        return path;
    }

    private string CopyFixture(string fixture, string name)
    {
        var path = _dir.Combine(name);
        File.Copy(Fixture(fixture), path);
        return path;
    }

    // Everything in the folder, files and folders, hidden ones included.
    private static string[] Entries(string folder) =>
        Directory.GetFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Is_named_and_placed_as_in_the_spec()
    {
        var op = Op();

        Assert.Equal("pdf.split", op.Id);
        Assert.Equal("Split into pages", op.MenuPath);
        Assert.Equal([".pdf"], op.Extensions);
        // One qpdf process per file, like the rotations.
        Assert.Equal(4, op.MaxParallel);
    }

    [Fact]
    public async Task Each_page_becomes_a_pdf_of_its_own_in_a_pages_folder_numbered_from_001()
    {
        var input = Pdf("report.pdf", 101, 102, 103, 104);

        var folder = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("report (pages)"), folder);
        Assert.Equal(["report 001.pdf", "report 002.pdf", "report 003.pdf", "report 004.pdf"], Entries(folder));
        Assert.Equal([[101], [102], [103], [104]], Entries(folder).Select(name => PageWidths(Path.Combine(folder, name))));
    }

    [Fact]
    public async Task Each_page_keeps_its_content_and_its_rotation()
    {
        var input = CopyFixture("rotations.pdf", "scan.pdf");

        var folder = await Op().RunAsync(input, CancellationToken.None);

        var corners = Entries(folder).Select(name =>
        {
            using var page = PdfDocument.Open(Path.Combine(folder, name));
            Assert.Equal(1, page.PageCount);
            return MarkCorner(page, 0);
        });
        Assert.Equal([Corner.TopLeft, Corner.TopRight, Corner.BottomRight, Corner.BottomLeft], corners);
    }

    [Fact]
    public async Task A_one_page_pdf_still_gives_a_folder_with_its_one_page()
    {
        var input = Pdf("card.pdf", 100);

        var folder = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(["card 001.pdf"], Entries(folder));
    }

    [Fact]
    public async Task Numbers_get_as_many_digits_as_the_page_count_needs()
    {
        var input = _dir.Combine("book.pdf");
        WriteBlank(input, 1_000, resources: true);

        var folder = await Op().RunAsync(input, CancellationToken.None);

        var names = Entries(folder);
        Assert.Equal(1_000, names.Length);
        Assert.Equal("book 0001.pdf", names[0]);
        Assert.Equal("book 0010.pdf", names[9]);
        Assert.Equal("book 1000.pdf", names[^1]);
    }

    [Fact]
    public async Task The_folder_never_replaces_or_merges_into_one_that_exists()
    {
        var input = Pdf("report.pdf", 100, 200);

        var first = await Op().RunAsync(input, CancellationToken.None);
        var second = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("report (pages)"), first);
        Assert.Equal(_dir.Combine("report (pages) (1)"), second);
        Assert.Equal(["report 001.pdf", "report 002.pdf"], Entries(second));
    }

    [Fact]
    public async Task A_long_name_is_shortened_so_every_page_name_fits()
    {
        // 251 + ".pdf" is the longest name a file can have; " 001.pdf" pushes a page name past 255, so it must be cut.
        var stem = string.Concat(Enumerable.Repeat("Ảnh chụp ", 30))[..251];
        var input = @"\\?\" + _dir.Combine(stem + ".pdf");
        WriteWidths(input, 100, 200, 300);

        var folder = await Op().RunAsync(input, CancellationToken.None);

        Assert.EndsWith(" (pages)", folder, StringComparison.Ordinal);
        Assert.Equal(255, Path.GetFileName(folder).Length);
        var names = Entries(@"\\?\" + folder);
        Assert.Equal(3, names.Length);
        Assert.All(names, name => Assert.Equal(255, name.Length));
        Assert.Equal([" 001.pdf", " 002.pdf", " 003.pdf"], names.Select(name => name[^8..]));
        Assert.StartsWith(stem[..200], names[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_cut_inside_a_character_outside_the_bmp_is_cut_before_it()
    {
        // The cut falls between the two halves of an emoji: the name keeps neither, never half of one.
        var stem = new string('a', 246) + "\U0001F9FE" + "bbb";
        Assert.Equal(251, stem.Length);
        var input = @"\\?\" + _dir.Combine(stem + ".pdf");
        WriteWidths(input, 100);

        var folder = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal([new string('a', 246) + " 001.pdf"], Entries(@"\\?\" + folder));
    }

    [Fact]
    public async Task The_pages_get_the_permissions_of_the_folder_like_any_new_file()
    {
        var input = Pdf("report.pdf", 100, 200);

        var folder = await Op().RunAsync(input, CancellationToken.None);

        var fresh = Path.Combine(folder, "fresh.txt");
        File.WriteAllText(fresh, "x");
        Assert.All(Entries(folder).Where(name => name != "fresh.txt"),
            name => Assert.Equal(Rules(fresh), Rules(Path.Combine(folder, name))));
    }

    private static string[] Rules(string path) => new FileInfo(path).GetAccessControl()
        .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .Select(rule => $"{rule.IdentityReference} {rule.AccessControlType} {rule.FileSystemRights} {rule.IsInherited}")
        .Order(StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public async Task The_original_is_left_byte_for_byte_unchanged()
    {
        var input = CopyFixture("rotations.pdf", "scan.pdf");
        var hash = SHA256.HashData(File.ReadAllBytes(input));
        var written = File.GetLastWriteTimeUtc(input);

        await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(input)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(input));
    }

    [Fact]
    public async Task Reports_progress_page_by_page_against_the_page_count()
    {
        var input = _dir.Combine("book.pdf");
        WriteBlank(input, 1_500, resources: true);
        var reports = new List<FileProgress>();
        var progress = new SynchronousProgress(reports.Add);

        await Op().RunAsync(input, progress, CancellationToken.None);

        Assert.Equal(new FileProgress("page", 1, 1_500), reports[0]);
        // qpdf takes a second or more for 1 500 pages, and the count is looked up five times a second.
        Assert.Contains(reports, report => report.Position > 1);
        Assert.All(reports, report => Assert.Equal(("page", 1_500), (report.Unit, report.Total)));
        Assert.Equal(reports.Select(r => r.Position).Order(), reports.Select(r => r.Position));
    }

    private sealed class SynchronousProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        private readonly Lock _gate = new();

        public void Report(FileProgress value)
        {
            lock (_gate)
                report(value);
        }
    }

    [Fact]
    public async Task A_password_protected_pdf_fails_with_the_password_message_and_leaves_nothing()
    {
        var input = CopyFixture("password-protected.pdf", "locked.pdf");

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.PasswordRequired, ex.Error);
        Assert.Equal(PdfException.NeedsPasswordMessage, ex.Message);
        Assert.Equal(["locked.pdf"], Entries(_dir.Path));
    }

    [Theory]
    [InlineData("This is a text file someone renamed to .pdf.\r\n")]
    [InlineData("")]
    [InlineData("%PDF-1.7\n1 0 obj\n<< /Type /Catalog")]
    public async Task A_file_that_is_not_a_pdf_fails_cleanly_and_leaves_nothing(string content)
    {
        var input = _dir.File("notes.pdf", content);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.NotPdf, ex.Error);
        Assert.Equal("This file is not a PDF, or it is damaged.", ex.Message);
        Assert.Equal(["notes.pdf"], Entries(_dir.Path));
    }

    [Fact]
    public async Task A_pdf_without_pages_fails_and_leaves_nothing()
    {
        var input = _dir.Combine("empty.pdf");
        WriteBlank(input, 0);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.NoPages, ex.Error);
        Assert.Equal("This PDF has no pages.", ex.Message);
        Assert.Equal(["empty.pdf"], Entries(_dir.Path));
    }

    [Fact]
    public async Task A_pdf_with_more_pages_than_the_limit_is_refused_before_any_page_is_written()
    {
        var input = _dir.Combine("huge.pdf");
        WriteBlank(input, PdfDocument.MaxPages + 1, resources: true);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.TooManyPages, ex.Error);
        Assert.Equal("This PDF has 2001 pages; Flevert works with at most 2000.", ex.Message);
        Assert.Equal(["huge.pdf"], Entries(_dir.Path));
    }

    [Fact]
    public async Task A_folder_whose_path_holds_percent_d_is_refused_rather_than_letting_qpdf_write_elsewhere()
    {
        // qpdf puts the page number in place of the first "%d" in the output path, wherever it is: here page 1 would go
        // to "50%done" with "%d" read as 1, the folder beside it.
        var folder = _dir.Combine("50%done");
        var decoy = _dir.Combine("501one");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(decoy);
        var input = Path.Combine(folder, "report.pdf");
        WriteWidths(input, 100, 200);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.UnsupportedFileName, ex.Error);
        Assert.Contains("%d", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["report.pdf"], Entries(folder));
        Assert.Empty(Entries(decoy));
    }

    [Fact]
    public async Task Works_on_a_pdf_whose_folder_and_name_hold_characters_outside_the_bmp()
    {
        var folder = _dir.Combine("Chuyến đi \U0001F34A Đà Lạt");
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "Hóa đơn \U0001F9FE.pdf");
        WriteWidths(input, 100, 200);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(Path.Combine(folder, "Hóa đơn \U0001F9FE (pages)"), output);
        Assert.Equal(["Hóa đơn \U0001F9FE 001.pdf", "Hóa đơn \U0001F9FE 002.pdf"], Entries(output));
    }

    [Fact]
    public async Task Works_through_the_runner_on_a_path_just_under_260_characters()
    {
        using var logs = new TempDir();
        var folder = LongPath(_dir.Path);
        while (folder.Length < 230)
            folder = Path.Combine(folder, "thư mục");
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, new string('a', 250 - folder.Length - ".pdf".Length - 1) + ".pdf");
        WriteWidths(input, 100, 200);
        Assert.Equal(250, input.Length);

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Path.Combine(folder, Path.GetFileNameWithoutExtension(input) + " (pages)"), result.Output);
        Assert.Equal(2, Directory.GetFiles(@"\\?\" + result.Output!).Length);
    }

    [Fact]
    public async Task Cancelling_while_qpdf_writes_the_pages_leaves_no_partial_folder()
    {
        var input = _dir.Combine("book.pdf");
        WriteBlank(input, PdfDocument.MaxPages, resources: true);
        using var cancel = new CancellationTokenSource();
        // Cancelled once the first page is out: qpdf is writing them.
        var progress = new SynchronousProgress(report =>
        {
            if (report.Position > 1)
                cancel.Cancel();
        });
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op().RunAsync(input, progress, cancel.Token));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}");
        Assert.Equal(["book.pdf"], Entries(_dir.Path));
    }
}
