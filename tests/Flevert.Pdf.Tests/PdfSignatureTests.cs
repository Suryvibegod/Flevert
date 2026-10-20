namespace Flevert.Pdf.Tests;

public sealed class PdfSignatureTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_signed_pdf_is_seen_and_an_unsigned_one_is_not()
    {
        var signed = MaliciousPdf.Write("signed", _dir.Combine("signed.pdf"));

        Assert.True(await PdfSignature.IsSignedAsync(signed, CancellationToken.None));
        Assert.False(await PdfSignature.IsSignedAsync(TestPdf.Fixture("three-colours.pdf"), CancellationToken.None));
    }

    [Theory]
    [InlineData((1 << 20) - 10)] // the key across the first two reads
    [InlineData((1 << 20) - 3)]
    [InlineData((3 << 20) + 7)] // in a later read
    public async Task The_key_is_found_wherever_it_falls_between_two_reads(int offset)
    {
        var path = _dir.Combine("big.pdf");
        var bytes = new byte[offset + 100];
        "/ByteRange"u8.CopyTo(bytes.AsSpan(offset));
        await File.WriteAllBytesAsync(path, bytes);

        Assert.True(await PdfSignature.IsSignedAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task A_key_cut_short_at_the_end_of_the_file_is_no_signature()
    {
        var path = _dir.Combine("cut.pdf");
        await File.WriteAllBytesAsync(path, [.. new byte[(1 << 20) - 4], .. "/ByteRang"u8]);

        Assert.False(await PdfSignature.IsSignedAsync(path, CancellationToken.None));
    }
}
