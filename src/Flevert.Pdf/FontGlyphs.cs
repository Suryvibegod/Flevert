using System.Buffers.Binary;

namespace Flevert.Pdf;

/// <summary>
/// Which characters a TrueType font has a glyph for, read from its character map (<c>cmap</c>, Unicode subtables of
/// format 12 or 4: the OpenType specification). For the watermark dialog, which refuses a character Windows' Arial would
/// draw as an empty box (docs/specs/pdf.md, v0.2.2). The font is Windows' own, so it is trusted; a map that cannot be read
/// is treated as unknown and checks nothing.
/// </summary>
internal sealed class FontGlyphs
{
    private static readonly Lazy<FontGlyphs?> LazyArial = new(() =>
    {
        try
        {
            return Read(File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    });

    private readonly Func<int, bool> _has;

    private FontGlyphs(Func<int, bool> has) => _has = has;

    /// <summary>Windows' Arial, or <c>null</c> when it is missing or its map cannot be read.</summary>
    public static FontGlyphs? Arial => LazyArial.Value;

    public bool Has(int codePoint) => _has(codePoint);

    /// <summary>The characters of <paramref name="text"/> the font has no glyph for, each once, in the order they come.</summary>
    public IReadOnlyList<string> Missing(string text)
    {
        var missing = new List<string>();
        foreach (var rune in text.EnumerateRunes())
        {
            var character = rune.ToString();
            if (!Has(rune.Value) && !missing.Contains(character, StringComparer.Ordinal))
                missing.Add(character);
        }

        return missing;
    }

    /// <summary>The glyphs of a TrueType font file, or <c>null</c> when it has no Unicode map this reads.</summary>
    public static FontGlyphs? Read(byte[] font)
    {
        try
        {
            var data = font.AsSpan();
            var tables = U16(data, 4);
            for (var i = 0; i < tables; i++)
            {
                var record = 12 + (16 * i);
                if (!data.Slice(record, 4).SequenceEqual("cmap"u8))
                    continue;
                var cmap = (int)U32(data, record + 8);
                return FromCmap(font, cmap);
            }

            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // The Windows Unicode subtables: full repertoire (3, 10) in format 12, else BMP (3, 1) in format 4.
    private static FontGlyphs? FromCmap(byte[] font, int cmap)
    {
        var data = font.AsSpan();
        var records = U16(data, cmap + 2);
        int? full = null, basic = null;
        for (var i = 0; i < records; i++)
        {
            var record = cmap + 4 + (8 * i);
            var (platform, encoding) = (U16(data, record), U16(data, record + 2));
            var subtable = cmap + (int)U32(data, record + 4);
            if (platform == 3 && encoding == 10 && U16(data, subtable) == 12)
                full = subtable;
            else if (platform == 3 && encoding == 1 && U16(data, subtable) == 4)
                basic = subtable;
        }

        if (full is { } twelve)
            return new FontGlyphs(Format12(font, twelve));
        return basic is { } four ? new FontGlyphs(Format4(font, four)) : null;
    }

    private static Func<int, bool> Format12(byte[] font, int at)
    {
        var data = font.AsSpan();
        var groups = U32(data, at + 12);
        var ranges = new List<(uint Start, uint End, uint Glyph)>();
        for (var i = 0L; i < groups; i++)
        {
            var group = checked((int)(at + 16 + (12 * i)));
            ranges.Add((U32(data, group), U32(data, group + 4), U32(data, group + 8)));
        }

        return codePoint => ranges.Any(range => codePoint >= range.Start && codePoint <= range.End && range.Glyph + ((uint)codePoint - range.Start) != 0);
    }

    private static Func<int, bool> Format4(byte[] font, int at)
    {
        var segments = U16(font, at + 6) / 2;
        var ends = at + 14;
        var starts = ends + (2 * segments) + 2;
        var deltas = starts + (2 * segments);
        var offsets = deltas + (2 * segments);
        return codePoint =>
        {
            if (codePoint > 0xFFFF)
                return false;
            try
            {
                for (var i = 0; i < segments; i++)
                {
                    if (codePoint > U16(font, ends + (2 * i)))
                        continue;
                    var start = U16(font, starts + (2 * i));
                    if (codePoint < start)
                        return false;
                    var delta = U16(font, deltas + (2 * i));
                    var offset = U16(font, offsets + (2 * i));
                    if (offset == 0)
                        return ((codePoint + delta) & 0xFFFF) != 0;
                    var glyph = U16(font, offsets + (2 * i) + offset + (2 * (codePoint - start)));
                    return glyph != 0 && ((glyph + delta) & 0xFFFF) != 0;
                }

                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                return true; // a map that cannot be read checks nothing
            }
        };
    }

    private static int U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at, 2));

    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at, 4));
}
