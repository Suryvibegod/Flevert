namespace Flevert.Pdf;

/// <summary>
/// Whether a PDF is digitally signed, so an operation that writes a new PDF from it (every PDF operation but Convert) can
/// say the signature is not valid in the output (docs/specs/pdf.md; T18). A signature covers the exact bytes of the file
/// it is in (its <c>/ByteRange</c>), and qpdf writes every byte anew.
/// </summary>
/// <remarks>
/// <para>
/// A signature dictionary is found by its <c>/ByteRange</c> key in the file's bytes, without parsing the PDF. The key has
/// to be there as it is: a signature names the byte ranges it covers around its own <c>/Contents</c>, so its dictionary
/// cannot sit inside a compressed object stream, and names are not encrypted. One read of the file, sharing it for reading
/// as qpdf does, instead of another qpdf process.
/// </para>
/// <para>
/// This is a note for the user, not a security decision: a key written with <c>#</c> escapes is missed, and a file that
/// merely mentions <c>/ByteRange</c> (in an uncompressed stream, say) gets the note without being signed.
/// </para>
/// </remarks>
public static class PdfSignature
{
    /// <summary>What the notification and the log say after a new PDF was written from a signed one.</summary>
    public const string NotValidNote = "Signed PDF: the digital signature is not valid in the output.";

    private const int ChunkBytes = 1 << 20;

    private static ReadOnlySpan<byte> Marker => "/ByteRange"u8;

    /// <summary>Whether <paramref name="path"/> holds a signature dictionary.</summary>
    public static async Task<bool> IsSignedAsync(string path, CancellationToken ct)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (file.ConfigureAwait(false))
        {
            var markerLength = Marker.Length;
            // The tail of each chunk is kept in front of the next, so a key across two chunks is still found.
            var buffer = new byte[ChunkBytes + markerLength];
            var kept = 0;
            while (true)
            {
                var read = await file.ReadAsync(buffer.AsMemory(kept, ChunkBytes), ct).ConfigureAwait(false);
                if (read == 0)
                    return false;
                var length = kept + read;
                if (buffer.AsSpan(0, length).IndexOf(Marker) >= 0)
                    return true;
                kept = Math.Min(markerLength - 1, length);
                buffer.AsSpan(length - kept, kept).CopyTo(buffer);
            }
        }
    }
}
