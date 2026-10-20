using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

public sealed class PdfRepairTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_repaired_output_with_other_than_the_pages_counted_fails()
    {
        // qpdf found three pages when it repaired the input that was counted with five.
        var output = _dir.Combine("out.pdf");
        WriteWidths(output, 100, 200, 300);

        var ex = await Assert.ThrowsAsync<PdfException>(() => PdfRepair.CheckAsync(Qpdf.NextToApp, repaired: true, pages: 5, output, CancellationToken.None));

        Assert.Equal(PdfError.MissingPages, ex.Error);
        Assert.Equal("This PDF is damaged: the result would miss pages.", ex.Message);
    }

    [Fact]
    public async Task A_repaired_output_with_the_pages_counted_passes()
    {
        var output = _dir.Combine("out.pdf");
        WriteWidths(output, 100, 200, 300);

        await PdfRepair.CheckAsync(Qpdf.NextToApp, repaired: true, pages: 3, output, CancellationToken.None);
    }

    [Fact]
    public async Task An_output_qpdf_did_not_repair_is_not_counted_again()
    {
        // Counting a file that is not there would fail: nothing is run.
        await PdfRepair.CheckAsync(Qpdf.NextToApp, repaired: false, pages: 5, _dir.Combine("none.pdf"), CancellationToken.None);
    }

    [Fact]
    public async Task Qpdf_reports_a_repair_only_when_it_had_to_make_one()
    {
        var sound = _dir.Combine("sound.pdf");
        WriteWidths(sound, 100);
        var damaged = MaliciousPdf.Write("xref-missing", _dir.Combine("damaged.pdf"));

        Assert.False(await Qpdf.NextToApp.TransformAsync(sound, _dir.Combine("a.pdf"), [Qpdf.KeepStreamsAsTheyAre], CancellationToken.None));
        Assert.True(await Qpdf.NextToApp.TransformAsync(damaged, _dir.Combine("b.pdf"), [Qpdf.KeepStreamsAsTheyAre], CancellationToken.None));
    }
}
