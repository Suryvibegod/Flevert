using System.Text;

namespace Flevert.Pdf.Tests;

/// <summary>
/// The requests and lines the v0.2.2 tools add to the PDF worker's protocol (<see cref="PdfToolsWorker"/>): a seventh
/// request field for their tasks only, and lines a worker may write only for the task it was given. The requests and
/// lines of 0.2.1 do not change.
/// </summary>
public sealed class PdfToolsProtocolTests
{
    public static TheoryData<string, string> Tasks() => new()
    {
        { "BlankPages", "" },
        { "Edit", new PdfEdit(PdfEditKind.Watermark) { Text = "Bản sao" }.Encode() },
        { "Edit", new PdfEdit(PdfEditKind.PageNumbers) { Position = PdfPageNumberPosition.TopRight, Style = PdfPageNumberStyle.Words }.Encode() },
        { "Edit", new PdfEdit(PdfEditKind.FourPerSheet).Encode() },
        { "ExtractImages", "" },
    };

    [Theory]
    [MemberData(nameof(Tasks))]
    public void A_request_of_a_new_task_carries_its_options(string task, string options)
    {
        var request = new PdfRenderRequest(@"C:\Hồ sơ\a.pdf", new FileIdentity(1, 2), @"C:\out", PdfPageImageFormat.Jpeg, 150) { Task = Enum.Parse<PdfWorkerTask>(task), Options = options };

        var decoded = PdfRenderRequest.Decode(request.Encode());

        Assert.Equal(request, decoded);
        // Each field ends with a NUL.
        Assert.Equal(7, Encoding.Unicode.GetString(request.Encode()).Count(c => c == '\0'));
    }

    [Fact]
    public void The_requests_of_the_tasks_before_keep_their_six_fields()
    {
        var request = new PdfRenderRequest(@"C:\a.pdf", new FileIdentity(1, 2), @"C:\out", PdfPageImageFormat.Png, 150) { Options = "ignored" };

        Assert.Equal("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0png\0150\0", Encoding.Unicode.GetString(request.Encode()));
    }

    [Theory]
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0edit\0150\0")] // no options
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0edit\0150\0rotate\0")] // an edit this version does not make
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0edit\0150\0watermark\u001F\0")] // no text
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0edit\0150\0numbers\u001F7\u001F0\0")]
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0blank\0150\0\0extra\0")]
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0png\0150\0\0")] // an old task with a seventh field
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0blank\0150\0something\0")] // options for a task that takes none
    [InlineData("flevert-pdf-worker/2\0C:\\a.pdf\01:2\0C:\\out\0images\0150\0something\0")]
    public void A_damaged_request_of_a_new_task_is_refused(string text) =>
        Assert.Throws<FormatException>(() => PdfRenderRequest.Decode(Encoding.Unicode.GetBytes(text)));

    private static PdfWorker.Session Session(PdfWorkerTask task, params string[] lines)
    {
        var session = new PdfWorker.Session(TestPdf.Worker, TimeSpan.FromMinutes(1), "read", null, CancellationToken.None) { Task = task };
        foreach (var line in lines)
            session.OnLine(line);
        return session;
    }

    [Fact]
    public void Blank_pages_are_said_between_a_page_and_its_end()
    {
        using var session = Session(PdfWorkerTask.BlankPages, "pages 3", "page 1 10 10", "wrote 1", "page 2 10 10", "blank 2", "wrote 2", "page 3 10 10", "blank 3", "wrote 3", "done 3");

        Assert.Equal([2, 3], session.Blank);
    }

    [Theory]
    [InlineData("Pictures", "pages 2", "page 1 10 10", "blank 1")] // not this task's line
    [InlineData("BlankPages", "pages 2", "page 1 10 10", "wrote 1", "blank 1")] // after the page
    [InlineData("BlankPages", "pages 2", "page 1 10 10", "blank 2")] // another page
    [InlineData("BlankPages", "pages 2", "page 1 10 10", "blank 1", "blank 1")] // twice
    [InlineData("Edit", "pages 1", "page 1 1 1", "saved 16")] // before every page is done
    [InlineData("Edit", "pages 1", "page 1 1 1", "wrote 1", "saved 16", "saved 16")] // not growing
    [InlineData("ExtractImages", "pages 1", "page 1 1 1", "images 1")] // before every page is seen
    [InlineData("ExtractImages", "pages 1", "page 1 1 1", "wrote 1", "images 1", "extract 2 10 10")]
    [InlineData("ExtractImages", "pages 1", "page 1 1 1", "wrote 1", "images 1", "extracted 1 png")]
    [InlineData("ExtractImages", "pages 1", "page 1 1 1", "wrote 1", "images 1", "extract 1 10 10", "extracted 1 gif")]
    [InlineData("ExtractImages", "pages 1", "page 1 1 1", "wrote 1", "images 1", "extract 1 10 10", "extracted 1 png", "extract 2 10 10")]
    [InlineData("ExtractImages", "pages 1", "page 1 1 1", "wrote 1", "images 2", "extract 1 10 10", "extracted 1 png", "done 1")] // one missing
    public void A_line_out_of_place_breaks_the_protocol(string task, params string[] lines)
    {
        var ex = Record.Exception(() => Session(Enum.Parse<PdfWorkerTask>(task), lines).Dispose());

        Assert.IsType<PdfWorker.ProtocolException>(ex);
    }

    [Fact]
    public void Images_are_said_one_at_a_time_with_their_kind()
    {
        var reports = new List<Flevert.Core.FileProgress>();
        var session = new PdfWorker.Session(TestPdf.Worker, TimeSpan.FromMinutes(1), "read", new Sync(reports.Add), CancellationToken.None)
        {
            Task = PdfWorkerTask.ExtractImages,
        };
        foreach (var line in new[] { "pages 1", "page 1 1 1", "wrote 1", "images 2", "extract 1 20000 20000", "extracted 1 jpg", "extract 2 5 5", "extracted 2 png", "done 1" })
            session.OnLine(line);

        Assert.Equal(["jpg", "png"], session.Extracted);
        Assert.Equal([new("page", 1, 1), new("image", 1, 2), new("image", 2, 2)], reports);
        session.Dispose();
    }

    private sealed class Sync(Action<Flevert.Core.FileProgress> report) : IProgress<Flevert.Core.FileProgress>
    {
        public void Report(Flevert.Core.FileProgress value) => report(value);
    }
}
