using System.Text;

namespace Flevert.Pdf.Tests;

/// <summary>
/// The worker side of the PDF worker, run in this process: the request it reads, the lines it writes, and the check that
/// it reads only the file the parent pinned (ADR-0008, "Reading untrusted PDFs in a worker").
/// </summary>
public sealed class PdfRenderWorkerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(@"C:\Hồ sơ\report.pdf")]
    [InlineData("C:\\odd \ud800 name\\a.pdf")] // a lone surrogate: legal in an NTFS name, not in UTF-8
    [InlineData("C:\\emoji \ud83d\ude00\\a=b.pdf")]
    [InlineData(@"\\?\C:\long\path.pdf")]
    public void The_request_carries_any_path_exactly(string path)
    {
        var request = new PdfRenderRequest(path, new FileIdentity(0x1234_5678_9ABC_DEF0, UInt128.MaxValue - 5), path + " (pages)",
            PdfPageImageFormat.Png, 37.5);

        var decoded = PdfRenderRequest.Decode(request.Encode());

        Assert.Equal(request, decoded);
    }

    [Theory]
    [InlineData("not a request")]
    // 0.2.0's version: fields have changed meaning since (the text and shrink tasks and their lines).
    [InlineData("flevert-pdf-worker/1\0C:\\a.pdf\01:2\0C:\\out\0png\0150\0")]
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0png\0")]
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0gif\0150\0")]
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0png\0-1\0")]
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0png\0NaN\0")]
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\0xyz\0C:\\out\0png\0150\0")]
    [InlineData("flevert-pdf-worker/2\0relative.pdf\01:2\0C:\\out\0png\0150\0")]
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0png\0150\0extra\0")]
    public void A_damaged_request_is_refused(string text)
    {
        Assert.Throws<FormatException>(() => PdfRenderRequest.Decode(Encoding.Unicode.GetBytes(text)));
    }

    [Fact]
    public void A_request_of_this_version_is_read_and_written()
    {
        var request = PdfRenderRequest.Decode(Encoding.Unicode.GetBytes("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0shrink\0150\0"));

        Assert.Equal(PdfWorkerTask.ShrinkImages, request.Task);
        Assert.StartsWith("flevert-pdf-worker/2\0", Encoding.Unicode.GetString(request.Encode()), StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_every_page_and_reports_each_step_in_order()
    {
        var input = Copy("three-colours.pdf", "colours.pdf");
        var folder = Folder();

        var (exit, lines) = Run(Request(input, folder, PdfPageImageFormat.Png, 150));

        Assert.Equal(0, exit);
        // Before each page: its number and the size it renders at; after: written. A4, Letter, 400 x 300 pt.
        Assert.Equal(["pages 3", "page 1 1240 1754", "wrote 1", "page 2 1275 1650", "wrote 2", "page 3 833 625", "wrote 3", "done 3"],
            lines.Where(line => !line.StartsWith("busy ", StringComparison.Ordinal)));
        Assert.Equal(["colours 001.png", "colours 002.png", "colours 003.png"], TestPdf.FileNames(folder));
    }

    [Fact]
    public void A_password_protected_pdf_is_an_error_line_with_the_shared_message()
    {
        var input = Copy("password-protected.pdf", "secret.pdf");
        var folder = Folder();

        var (exit, lines) = Run(Request(input, folder, PdfPageImageFormat.Jpeg, 150));

        Assert.Equal(1, exit);
        Assert.Equal(["error PasswordRequired This PDF needs a password: use Remove password first"], lines);
        Assert.Empty(TestPdf.FileNames(folder));
    }

    [Fact]
    public void Refuses_a_file_other_than_the_one_the_parent_pinned()
    {
        // The parent names the file by path and by identity; a path that leads elsewhere is not read.
        var pinned = Copy("three-colours.pdf", "pinned.pdf");
        var other = Copy("three-colours.pdf", "other.pdf");
        var folder = Folder();
        var request = Request(pinned, folder, PdfPageImageFormat.Png, 150) with { InputPath = other };

        var (exit, lines) = Run(request);

        Assert.Equal(2, exit);
        Assert.Empty(lines);
        Assert.Empty(TestPdf.FileNames(folder));
    }

    [Fact]
    public void A_request_it_cannot_read_ends_the_worker_without_reading_anything()
    {
        var (exit, lines) = Run(Encoding.Unicode.GetBytes("flevert-pdf-worker/1\0garbage"));

        Assert.Equal(2, exit);
        Assert.Empty(lines);
    }

    [Fact]
    public void The_identity_of_a_file_is_the_same_through_every_handle_and_differs_between_files()
    {
        var a = Copy("three-colours.pdf", "a.pdf");
        var b = Copy("three-colours.pdf", "b.pdf");
        using var a1 = File.OpenHandle(a);
        using var a2 = File.OpenHandle(a);
        using var b1 = File.OpenHandle(b);

        Assert.Equal(FileIdentity.Of(a1), FileIdentity.Of(a2));
        Assert.NotEqual(FileIdentity.Of(a1), FileIdentity.Of(b1));
        Assert.Equal(FileIdentity.Of(a1), FileIdentity.Parse(FileIdentity.Of(a1).ToString()));
    }

    private string Copy(string fixture, string name)
    {
        var path = _dir.Combine(name);
        File.Copy(TestPdf.Fixture(fixture), path);
        return path;
    }

    private string Folder()
    {
        var folder = _dir.Combine(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static PdfRenderRequest Request(string input, string folder, PdfPageImageFormat format, double dpi)
    {
        using var handle = File.OpenHandle(input);
        return new PdfRenderRequest(input, FileIdentity.Of(handle), folder, format, dpi);
    }

    private static (int Exit, string[] Lines) Run(PdfRenderRequest request) => Run(request.Encode());

    private static (int Exit, string[] Lines) Run(byte[] request)
    {
        var output = new MemoryStream();
        var exit = PdfRenderWorker.Run(new MemoryStream(request), output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        return (exit, text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
