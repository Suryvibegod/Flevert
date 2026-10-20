using System.Text.Json;
using System.Text.Json.Nodes;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>The reader of qpdf's description of a PDF, which goes through the file one entry at a time.</summary>
public sealed class QpdfJsonFileTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<string> Describe(string fixture)
    {
        var json = _dir.Combine("pdf.json");
        var run = await ExternalTool.RunAsync(Qpdf.NextToApp.ExePath,
            ["--json-output=2", "--json-stream-data=none", Fixture(fixture), json], TimeSpan.FromSeconds(30));
        Assert.True(run.ExitCode == 0, run.StandardError);
        return json;
    }

    private static List<(int Element, string Key, string Value)> ReadAll(string path, int initialBufferBytes, int maxEntryBytes)
    {
        var entries = new List<(int, string, string)>();
        QpdfJsonFile.Read(path, (element, key, value) => entries.Add((element, key, value?.ToJsonString() ?? "null")),
            initialBufferBytes, maxEntryBytes, CancellationToken.None);
        return entries;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(1 << 20)]
    public async Task Every_entry_comes_out_whole_whatever_the_size_of_the_first_buffer(int initialBufferBytes)
    {
        var path = await Describe("metadata.pdf");
        using var whole = JsonDocument.Parse(File.ReadAllBytes(path));
        var expected = whole.RootElement.GetProperty("qpdf").EnumerateArray()
            .SelectMany((element, index) => element.EnumerateObject()
                .Select(entry => (index, entry.Name, JsonNode.Parse(entry.Value.GetRawText())!.ToJsonString())))
            .ToList();

        var entries = ReadAll(path, initialBufferBytes, 1 << 20);

        Assert.Equal(expected, entries);
        Assert.Contains(entries, entry => entry is (QpdfJsonFile.Header, "maxobjectid", "19"));
        Assert.Contains(entries, entry => entry.Element == QpdfJsonFile.Objects && entry.Key == "trailer");
    }

    [Fact]
    public async Task An_entry_larger_than_the_largest_buffer_is_refused()
    {
        var path = await Describe("metadata.pdf");

        var ex = Assert.Throws<InvalidDataException>(() => ReadAll(path, 64, 256));

        Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_description_cut_short_is_refused()
    {
        var path = await Describe("metadata.pdf");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.Throws<InvalidDataException>(() => ReadAll(path, 1 << 20, 1 << 20));
    }

    [Fact]
    public async Task Cancelling_stops_the_reading()
    {
        var path = await Describe("metadata.pdf");
        using var cancel = new CancellationTokenSource();
        var read = 0;

        Assert.Throws<OperationCanceledException>(() => QpdfJsonFile.Read(path, (_, _, _) =>
        {
            if (++read == 3)
                cancel.Cancel();
        }, 16, 1 << 20, cancel.Token));

        Assert.Equal(3, read);
    }
}
