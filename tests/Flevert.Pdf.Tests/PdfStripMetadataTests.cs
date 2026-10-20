using System.Security.Cryptography;
using System.Text.Json;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

public sealed class PdfStripMetadataTests : IDisposable
{
    // What metadata.pdf holds that Remove metadata must take out (fixtures/README.md): document information, XMP of the
    // document, a page and an image, private application data, an attachment and every kind of JavaScript in it.
    private static readonly string[] Secrets =
    [
        "Nguyen Van A", "Secret plan", "Taxes", "(private)", "Writer", "Some PDF Library", "Example Ltd", "D:2024",
        "Catalog XMP Author", "Page XMP Author", "Image XMP Author", "/Metadata",
        "catalog piece", "page piece", "/PieceInfo",
        "Attached secret text", "secret.txt", "/EmbeddedFile", "/FileAttachment",
        "app.alert", "/JavaScript", "/JS", "/OpenAction",
    ];

    private readonly TempDir _dir = new();
    private readonly TempDir _scratch = new();

    public void Dispose()
    {
        _dir.Dispose();
        _scratch.Dispose();
    }

    // The operation's private folders go under _scratch, so a test can see that it removes them.
    private PdfStripMetadata Op() => new(Qpdf.NextToApp) { ScratchRoot = _scratch.Path };

    private string CopyFixture(string fixture, string name)
    {
        var path = _dir.Combine(name);
        File.Copy(Fixture(fixture), path);
        return path;
    }

    [Fact]
    public void It_is_named_and_placed_as_in_the_spec()
    {
        var op = PdfOperations.All.OfType<IOperation>().Single(op => op.Id == "pdf.strip-metadata");

        Assert.Equal("Remove metadata", op.MenuPath);
        Assert.Equal([".pdf"], op.Extensions);
        // Two single-threaded qpdf runs one after the other per file, like the rotations.
        Assert.Equal(4, op.MaxParallel);
    }

    [Fact]
    public async Task The_fixture_holds_everything_the_operation_must_remove()
    {
        // Without this, a test below could pass because there was nothing to remove.
        var text = await QdfText(Fixture("metadata.pdf"));

        Assert.All(Secrets, secret => Assert.Contains(secret, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Information_xmp_private_data_attachments_and_javascript_are_gone()
    {
        var input = CopyFixture("metadata.pdf", "report.pdf");

        var output = await Op().RunAsync(input, CancellationToken.None);

        var text = await QdfText(output);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, text, StringComparison.Ordinal));
        var list = await ExternalTool.RunAsync(Qpdf.NextToApp.ExePath, ["--list-attachments", output], TimeSpan.FromSeconds(30));
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("no embedded files", list.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_document_information_is_only_producer_flevert()
    {
        var input = CopyFixture("metadata.pdf", "report.pdf");

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(new Dictionary<string, string> { ["/Producer"] = "u:Flevert" }, await ReadInfo(output));
    }

    [Fact]
    public async Task A_pdf_without_document_information_gets_producer_flevert()
    {
        var input = CopyFixture("three-colours.pdf", "plain.pdf");
        Assert.Null(await ReadInfo(input));

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(new Dictionary<string, string> { ["/Producer"] = "u:Flevert" }, await ReadInfo(output));
    }

    [Fact]
    public async Task Information_written_inside_the_trailer_is_replaced_too()
    {
        var input = _dir.Combine("direct info.pdf");
        WriteWithInfoInTrailer(input, "<< /Author (Nguyen Van A) /Producer (Writer) >>");
        Assert.Equal("u:Nguyen Van A", (await ReadInfo(input))!["/Author"]);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(new Dictionary<string, string> { ["/Producer"] = "u:Flevert" }, await ReadInfo(output));
        Assert.DoesNotContain("Nguyen Van A", await QdfText(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_object_nested_as_deeply_as_qpdf_reads_is_rewritten()
    {
        var input = _dir.Combine("deep.pdf");
        WriteWithDeepCatalog(input, 490);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var text = await QdfText(output);
        Assert.DoesNotContain("deep piece", text, StringComparison.Ordinal);
        // QDF writes each level of an array on a line of its own.
        Assert.True(text.Count(c => c == '[') >= 490, "the nested array is gone");
    }

    [Fact]
    public async Task The_page_looks_the_same_and_bookmarks_and_links_stay()
    {
        var input = CopyFixture("metadata.pdf", "report.pdf");

        var output = await Op().RunAsync(input, CancellationToken.None);

        using (var before = PdfDocument.Open(input))
        using (var after = PdfDocument.Open(output))
        {
            Assert.Equal(before.PageCount, after.PageCount);
            var (was, now) = (before.Render(0, 72), after.Render(0, 72));
            Assert.Equal(was.Bgra.ToArray(), now.Bgra.ToArray());
            // Not blank: the blue rectangle, and the image's red pixel drawn over it.
            Assert.Equal((0, 0, 255, 255), Pixel(now, 60, 240));
            Assert.Equal((255, 0, 0, 255), Pixel(now, 160, 110));
        }

        var text = await QdfText(output);
        Assert.Contains("(Chapter 1)", text, StringComparison.Ordinal);
        // The link lost its script and was written again: its text outside ASCII came through the update unchanged.
        var link = (await QpdfObjects(output)).EnumerateObject()
            .Select(entry => entry.Value.TryGetProperty("value", out var value) ? value : default)
            .Single(value => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("/Subtype", out var type) && type.GetString() == "/Link");
        Assert.Equal("u:Liên kết \U0001F517", link.GetProperty("/Contents").GetString());
        Assert.False(link.TryGetProperty("/A", out _));
    }

    [Fact]
    public async Task The_output_is_named_no_metadata_beside_the_original_and_never_replaces_a_file()
    {
        var input = CopyFixture("metadata.pdf", "invoice.pdf");

        var first = await Op().RunAsync(input, CancellationToken.None);
        var second = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("invoice (no metadata).pdf"), first);
        Assert.Equal(_dir.Combine("invoice (no metadata) (1).pdf"), second);
        // Nothing else beside the input, hidden or not: qpdf's description of the PDF, which repeats its metadata, went
        // into a private folder, and that is gone too.
        Assert.Equal(["invoice (no metadata) (1).pdf", "invoice (no metadata).pdf", "invoice.pdf"], FileNames(_dir.Path));
        Assert.Empty(Directory.GetDirectories(_dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(_scratch.Path));
    }

    [Fact]
    public async Task The_original_is_left_byte_for_byte_unchanged()
    {
        var input = CopyFixture("metadata.pdf", "report.pdf");
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
        Assert.Empty(Directory.GetFileSystemEntries(_scratch.Path));
    }

    [Theory]
    [InlineData("This is a text file someone renamed to .pdf.\r\n")]
    [InlineData("")]
    public async Task A_file_that_is_not_a_pdf_fails_cleanly_and_leaves_nothing(string content)
    {
        var input = _dir.File("notes.pdf", content);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.NotPdf, ex.Error);
        Assert.Equal(["notes.pdf"], FileNames(_dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(_scratch.Path));
    }

    [Fact]
    public async Task Works_on_a_pdf_at_a_long_path_whose_names_are_not_ascii()
    {
        var folder = @"\\?\" + _dir.Path;
        while (folder.Length < 320)
            folder = Path.Combine(folder, "thư mục con rất dài để vượt quá giới hạn MAX_PATH \U0001F34A");
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "Hóa đơn — 中文 \U0001F9FE.pdf");
        File.Copy(Fixture("metadata.pdf"), input);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(Path.Combine(folder, "Hóa đơn — 中文 \U0001F9FE (no metadata).pdf"), output);
        // qpdf reads the copy at the short path: the output at the long one is what the operation wrote.
        var copy = _dir.Combine("copy.pdf");
        File.Copy(output, copy);
        Assert.Equal(new Dictionary<string, string> { ["/Producer"] = "u:Flevert" }, await ReadInfo(copy));
    }

    [Fact]
    public async Task Cancelling_stops_qpdf_and_leaves_nothing()
    {
        var input = _dir.Combine("big.pdf");
        WriteBlank(input, 200_000, resources: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op().RunAsync(input, cancel.Token));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
        Assert.Equal(["big.pdf"], FileNames(_dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(_scratch.Path));
    }

    [Fact]
    public async Task Works_through_the_runner_and_reports_the_output()
    {
        using var logs = new TempDir();
        var input = CopyFixture("metadata.pdf", "Báo cáo.pdf");

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(_dir.Combine("Báo cáo (no metadata).pdf"), result.Output);
    }

    [Fact]
    public void The_private_folder_is_only_for_this_user_system_and_administrators()
    {
        var folder = Path.Combine(_scratch.Path, "private");

        PrivateFolder.Create(folder);

        var rules = new DirectoryInfo(folder).GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .ToList();
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.All(rules, rule => Assert.False(rule.IsInherited));
        Assert.Equal(
            new[] { me.User!.Value, "S-1-5-18", "S-1-5-32-544" }.Order(StringComparer.Ordinal),
            rules.Select(rule => rule.IdentityReference.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Creating_a_private_folder_where_one_exists_fails()
    {
        // Someone who guessed the name and made the folder first must not get qpdf to write into theirs.
        var folder = Path.Combine(_scratch.Path, "taken");
        Directory.CreateDirectory(folder);

        Assert.Throws<IOException>(() => PrivateFolder.Create(folder));
    }

    [Fact]
    public async Task The_json_update_is_valid_for_qpdf_when_nothing_needs_removing()
    {
        // A PDF with nothing to remove still gets a well-formed update (just the producer), and qpdf --check passes.
        var input = _dir.Combine("blank.pdf");
        WriteBlank(input, 2, resources: true);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var check = await ExternalTool.RunAsync(Qpdf.NextToApp.ExePath, ["--check", output], TimeSpan.FromSeconds(30));
        Assert.True(check.ExitCode == 0, check.StandardError + check.StandardOutput);
        Assert.Equal(2, (await QpdfObjects(output)).EnumerateObject().Count(o => o.Value.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("/Type", out var t) && t.GetString() == "/Page"));
    }
}
