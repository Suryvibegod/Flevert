using System.Buffers.Binary;

namespace Flevert.Pdf.Tests;

/// <summary>The character map FontGlyphs reads for the watermark dialog, from fonts built here.</summary>
public sealed class FontGlyphsTests
{
    [Fact]
    public void A_format_12_map_says_which_characters_have_a_glyph()
    {
        var glyphs = FontGlyphs.Read(Font(groups: 1, ('A', 'Z', 1)));

        Assert.NotNull(glyphs);
        Assert.True(glyphs.Has('A'));
        Assert.True(glyphs.Has('Z'));
        Assert.False(glyphs.Has('a'));
        Assert.Equal(["a", "😀"], glyphs.Missing("Aa😀a"));
    }

    [Theory]
    [InlineData(2u)] // more groups than the map holds
    [InlineData(0x1555_5555u)] // a count whose last group lies past 2 GB
    [InlineData(uint.MaxValue)]
    public void A_map_that_claims_more_groups_than_it_holds_is_unreadable(uint groups)
    {
        Assert.Null(FontGlyphs.Read(Font(groups, ('A', 'Z', 1))));
    }

    // A font of one table, cmap, with one Windows full-repertoire subtable (3, 10) of format 12 holding the given groups
    // but claiming a count of groups.
    private static byte[] Font(uint groups, params (uint Start, uint End, uint Glyph)[] held)
    {
        const int Cmap = 28, Subtable = 12;
        var font = new byte[Cmap + Subtable + 16 + (12 * held.Length)];
        var data = font.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x0001_0000);
        BinaryPrimitives.WriteUInt16BigEndian(data[4..], 1);
        "cmap"u8.CopyTo(data[12..]);
        BinaryPrimitives.WriteUInt32BigEndian(data[20..], Cmap);
        BinaryPrimitives.WriteUInt32BigEndian(data[24..], (uint)(font.Length - Cmap));
        BinaryPrimitives.WriteUInt16BigEndian(data[(Cmap + 2)..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(data[(Cmap + 4)..], 3);
        BinaryPrimitives.WriteUInt16BigEndian(data[(Cmap + 6)..], 10);
        BinaryPrimitives.WriteUInt32BigEndian(data[(Cmap + 8)..], Subtable);
        var at = Cmap + Subtable;
        BinaryPrimitives.WriteUInt16BigEndian(data[at..], 12);
        BinaryPrimitives.WriteUInt32BigEndian(data[(at + 12)..], groups);
        for (var i = 0; i < held.Length; i++)
        {
            var group = at + 16 + (12 * i);
            BinaryPrimitives.WriteUInt32BigEndian(data[group..], held[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(data[(group + 4)..], held[i].End);
            BinaryPrimitives.WriteUInt32BigEndian(data[(group + 8)..], held[i].Glyph);
        }

        return font;
    }
}
