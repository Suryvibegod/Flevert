using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flevert.Pdf;

/// <summary>
/// Reads the description of a PDF that <c>qpdf --json-output=2</c> writes: <c>{"qpdf": [header, objects]}</c>, the
/// header holding <c>jsonversion</c>, <c>maxobjectid</c> and the like, the objects keyed <c>obj:N G R</c> and
/// <c>trailer</c>. One entry at a time, so memory follows the largest object rather than the whole PDF (500 000 pages
/// make a description of hundreds of megabytes).
/// </summary>
internal static class QpdfJsonFile
{
    /// <summary>The header, the first element of the "qpdf" array.</summary>
    public const int Header = 0;

    /// <summary>The objects and the trailer, the second element of the "qpdf" array.</summary>
    public const int Objects = 1;

    // qpdf 12.4.2 refuses objects nested more than 500 deep (checked: 490 is described, 510 is an error); the
    // description adds a few levels of its own.
    internal const int MaxDepth = 600;

    private const int InitialBufferBytes = 1 << 20;

    // A single object larger than this is not a PDF anyone made by hand or by a tool for people.
    internal const int MaxEntryBytes = 256 << 20;

    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = MaxDepth };

    /// <summary>
    /// Calls <paramref name="visit"/> for each entry of the header and of the objects, in file order, with the element
    /// (<see cref="Header"/> or <see cref="Objects"/>), the key and the value.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a complete description in this form, or one entry is larger than <see cref="MaxEntryBytes"/>.</exception>
    public static void Read(string path, Action<int, string, JsonNode?> visit, CancellationToken ct) =>
        Read(path, visit, InitialBufferBytes, MaxEntryBytes, ct);

    /// <summary>As <see cref="Read(string, Action{int, string, JsonNode}, CancellationToken)"/>, with the buffer's first and largest sizes given (for tests).</summary>
    internal static void Read(string path, Action<int, string, JsonNode?> visit, int initialBufferBytes, int maxEntryBytes, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        var buffer = new byte[initialBufferBytes];
        var length = 0;
        var final = false;
        var position = new Position(new JsonReaderState(ReaderOptions));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!final)
            {
                if (length == buffer.Length)
                {
                    // Nothing could be read from a full buffer: one entry is larger than it.
                    if (buffer.Length >= maxEntryBytes)
                        throw new InvalidDataException("An object in qpdf's description of the PDF is too large to read.");
                    Array.Resize(ref buffer, Math.Min(buffer.Length * 2, maxEntryBytes));
                }

                var read = file.Read(buffer, length, buffer.Length - length);
                if (read == 0)
                    final = true;
                length += read;
            }

            int consumed;
            try
            {
                consumed = ReadEntries(buffer.AsSpan(0, length), final, ref position, visit, ct);
            }
            catch (JsonException e)
            {
                throw new InvalidDataException("qpdf's description of the PDF is not valid JSON.", e);
            }

            if (position.Done)
                return;
            if (final)
                throw new InvalidDataException("qpdf's description of the PDF ended before it was complete.");
            buffer.AsSpan(consumed, length - consumed).CopyTo(buffer);
            length -= consumed;
        }
    }

    // Where the reader is, kept between blocks of the file.
    private struct Position(JsonReaderState state)
    {
        public JsonReaderState State = state;
        public bool InQpdf;
        public int Element = -1;
        public bool Done;
    }

    // Reads the complete tokens and entries in data and returns how many bytes that was; an entry not yet whole is left
    // for the next call, with more of the file.
    private static int ReadEntries(
        ReadOnlySpan<byte> data, bool final, ref Position position, Action<int, string, JsonNode?> visit, CancellationToken ct)
    {
        var reader = new Utf8JsonReader(data, final, position.State);
        var consumed = 0;
        while (true)
        {
            var before = reader;
            if (!reader.Read())
                break;
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    position.InQpdf = reader.ValueTextEquals("qpdf"u8);
                    break;
                case JsonTokenType.StartObject when reader.CurrentDepth == 2 && position.InQpdf:
                    position.Element++;
                    break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 3 && position.InQpdf && position.Element is Header or Objects:
                    var probe = reader;
                    if (!probe.TrySkip())
                    {
                        // The value is not all here yet: read this entry again, name and all, with more of the file.
                        reader = before;
                        position.State = reader.CurrentState;
                        return consumed;
                    }

                    ct.ThrowIfCancellationRequested();
                    var key = reader.GetString()!;
                    reader.Read();
                    visit(position.Element, key, JsonNode.Parse(ref reader));
                    break;
                case JsonTokenType.EndObject when reader.CurrentDepth == 0:
                    position.Done = true;
                    return (int)reader.BytesConsumed;
            }

            consumed = (int)reader.BytesConsumed;
            position.State = reader.CurrentState;
        }

        return consumed;
    }
}
