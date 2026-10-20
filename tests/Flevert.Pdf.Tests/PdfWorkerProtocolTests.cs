namespace Flevert.Pdf.Tests;

/// <summary>
/// What flevert.exe makes of the lines a worker writes (<see cref="PdfRenderWorker"/>), without a worker: a worker that
/// misbehaves (a bug, or pdfium's memory overwritten by a hostile PDF) must end in <see cref="PdfWorker.ProtocolException"/>,
/// which fails the file as <see cref="PdfError.ReaderFailed"/>, never in another exception.
/// </summary>
public sealed class PdfWorkerProtocolTests : IDisposable
{
    private static readonly string Hash = new('a', 64);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private PdfWorker.Session Shrinking()
    {
        var session = new PdfWorker.Session(TestPdf.Worker, TimeSpan.FromMinutes(1), "read", null, CancellationToken.None) { ShrinkFolder = _dir.Path };
        foreach (var line in new[] { "pages 1", "page 1 1 1", "wrote 1" })
            session.OnLine(line);
        return session;
    }

    [Theory]
    [InlineData("10000 10000")] // 100 megapixels, the most the worker decodes
    [InlineData("1 100000000")]
    public void An_image_of_at_most_100_megapixels_may_be_encoded(string size)
    {
        using var session = Shrinking();

        session.OnLine($"encode {Hash} {size}");
        session.OnLine($"image {Hash} 1 1 gray");

        Assert.Equal(Hash, Assert.Single(session.Shrunk).Hash);
    }

    [Theory]
    [InlineData("encode {0} 10001 10000")]
    [InlineData("encode {0} 100000000 100000000")]
    [InlineData("encode {0} 0 1")]
    public void An_encode_line_past_100_megapixels_breaks_the_protocol(string line)
    {
        using var session = Shrinking();

        var ex = Record.Exception(() => session.OnLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, line, Hash)));

        Assert.IsType<PdfWorker.ProtocolException>(ex);
    }

    [Theory]
    [InlineData("image {0} 10001 10000 rgb")]
    [InlineData("image {0} 100000000 100000000 rgb")]
    public void An_image_line_past_100_megapixels_breaks_the_protocol(string line)
    {
        using var session = Shrinking();
        session.OnLine($"encode {Hash} 20000 5000");

        var ex = Record.Exception(() => session.OnLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, line, Hash)));

        Assert.IsType<PdfWorker.ProtocolException>(ex);
    }
}
