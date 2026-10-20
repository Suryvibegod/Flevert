using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Flevert.Pdf.Tests;

/// <summary>
/// <see cref="ImageReplacement"/> on descriptions written here in the form qpdf writes them (<c>--json-output=2</c>, stream
/// data in files), so each rule is seen alone; <see cref="PdfCompressSharingTests"/> runs it on real PDFs through qpdf.
/// </summary>
public sealed class ImageReplacementTests : IDisposable
{
    private const string Image = """{"/Type": "/XObject", "/Subtype": "/Image", "/Width": 2480, "/Height": 3508, "/ColorSpace": "/DeviceRGB", "/BitsPerComponent": 8, "/Filter": "/DCTDecode", "/Length": 4096}""";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Prefix => _dir.Combine("s");

    [Fact]
    public void An_image_whose_data_the_worker_shrank_gets_the_jpeg_and_keeps_the_rest_of_its_dictionary()
    {
        var hash = Data(5, 4096);
        var described = Describe((5, WithEntry(Image, "/SMask", "6 0 R")));

        var (replaced, update) = Run(described, [Shrunk(hash, 1000)]);

        Assert.Equal(1, replaced);
        var dictionary = update["obj:5 0 R"]!["stream"]!["dict"]!;
        Assert.Equal("/DCTDecode", (string?)dictionary["/Filter"]);
        Assert.Equal(1240, (int?)dictionary["/Width"]);
        Assert.Equal("6 0 R", (string?)dictionary["/SMask"]);
    }

    [Fact]
    public void Images_with_the_same_data_but_different_dictionaries_are_none_of_them_replaced()
    {
        var hash = Data(5, 4096);
        File.Copy($"{Prefix}-5", $"{Prefix}-7");
        var described = Describe((5, Image), (7, WithEntry(Image, "/Decode", new JsonArray(1, 0, 1, 0, 1, 0))));

        var (replaced, update) = Run(described, [Shrunk(hash, 1000)]);

        Assert.Equal(0, replaced);
        Assert.Empty(update);
    }

    [Fact]
    public void An_image_whose_dictionary_is_over_64_kb_is_left_as_it_is()
    {
        // Nothing of the PDF that large is kept in flevert.exe, which has no memory cap of its own.
        var small = Data(5, 4096);
        var large = Data(7, 4097);
        var described = Describe((5, Image), (7, WithEntry(Image, "/Junk", new string('x', 64 * 1024))));

        var (replaced, update) = Run(described, [Shrunk(small, 1000), Shrunk(large, 1000)]);

        Assert.Equal(1, replaced);
        Assert.Equal(["obj:5 0 R"], update.Select(entry => entry.Key));
    }

    [Fact]
    public void An_oversized_dictionary_with_the_same_data_as_another_image_keeps_both_as_they_are()
    {
        // Its dictionary was not kept, so it cannot be told to be the same as the other's: neither is replaced.
        var hash = Data(5, 4096);
        File.Copy($"{Prefix}-5", $"{Prefix}-7");
        var described = Describe((5, Image), (7, WithEntry(Image, "/Junk", new string('x', 64 * 1024))));

        var (replaced, _) = Run(described, [Shrunk(hash, 1000)]);

        Assert.Equal(0, replaced);
    }

    [Fact]
    public void Past_the_most_images_it_matches_the_rest_are_left_as_they_are()
    {
        var images = Enumerable.Range(1, 3).Select(i => (Number: 10 + i, Hash: Data(10 + i, 4000 + i))).ToList();
        var described = Describe([.. images.Select(image => (image.Number, Image))]);

        var (replaced, update) = Run(described, [.. images.Select(image => Shrunk(image.Hash, 1000))], maxMatches: 2);

        Assert.Equal(2, replaced);
        Assert.Equal(["obj:11 0 R", "obj:12 0 R"], update.Select(entry => entry.Key));
    }

    [Fact]
    public void A_jpeg_no_smaller_than_the_stored_data_is_not_used()
    {
        var hash = Data(5, 4096);
        var described = Describe((5, Image));

        var (replaced, _) = Run(described, [Shrunk(hash, 4096)]);

        Assert.Equal(0, replaced);
    }

    [Fact]
    public void An_image_no_page_draws_from_its_resources_is_left_as_it_is()
    {
        var hash = Data(5, 4096);
        var described = Describe([], [(1, """{"/Type": "/Page"}""")], [(5, Image)]);

        Assert.Equal(0, Run(described, [Shrunk(hash, 1000)]).Replaced);
    }

    [Fact]
    public void An_image_the_page_tree_gives_every_page_is_replaced()
    {
        // Resources inherited from the page tree node, as qpdf lists them for the page.
        var hash = Data(5, 4096);
        var described = Describe([5],
            [(1, """{"/Type": "/Page", "/Parent": "2 0 R"}"""), (2, """{"/Type": "/Pages", "/Kids": ["1 0 R"], "/Resources": {"/XObject": {"/Im0": "5 0 R"}}}""")],
            [(5, Image)]);

        Assert.Equal(1, Run(described, [Shrunk(hash, 1000)]).Replaced);
    }

    [Fact]
    public void An_image_held_by_dictionaries_only_the_page_uses_is_replaced()
    {
        // The control for the three below: resources and XObject dictionaries that are objects of their own.
        var hash = Data(5, 4096);
        var described = Describe([5],
            [(1, """{"/Type": "/Page", "/Resources": "3 0 R"}"""), (3, """{"/XObject": "4 0 R"}"""), (4, """{"/Im0": "5 0 R"}""")],
            [(5, Image)]);

        Assert.Equal(1, Run(described, [Shrunk(hash, 1000)]).Replaced);
    }

    [Theory]
    [InlineData("""{"/Type": "/XObject", "/Subtype": "/Form", "/Resources": {"/XObject": {"/Stamp": "5 0 R"}}}""")] // an appearance of its own
    [InlineData("""{"/Type": "/Pattern", "/Resources": "3 0 R"}""")] // the page's resources, shared
    [InlineData("""{"/Type": "/XObject", "/Subtype": "/Form", "/Resources": {"/XObject": "4 0 R"}}""")] // the page's XObject dictionary, shared
    public void An_image_something_besides_the_page_may_draw_is_left_as_it_is(string other)
    {
        // An annotation's appearance, a pattern or a soft mask group draws it where the worker does not look.
        var hash = Data(5, 4096);
        var described = Describe([5],
            [(1, """{"/Type": "/Page", "/Resources": "3 0 R"}"""), (3, """{"/XObject": "4 0 R"}"""), (4, """{"/Im0": "5 0 R"}"""), (9, other)],
            [(5, Image)]);

        Assert.Equal(0, Run(described, [Shrunk(hash, 1000)]).Replaced);
    }

    // Writes the stored data of object number, as qpdf would, and returns its SHA-256.
    private string Data(int number, int length)
    {
        var data = new byte[length];
        new Random(number).NextBytes(data);
        File.WriteAllBytes($"{Prefix}-{number}", data);
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    private PdfShrunkImage Shrunk(string hash, int bytes)
    {
        var path = _dir.Combine(hash + ".jpg");
        File.WriteAllBytes(path, new byte[bytes]);
        return new PdfShrunkImage(hash, path, 1240, 1754, Gray: false);
    }

    private static string WithEntry(string dictionary, string key, JsonNode value)
    {
        var node = JsonNode.Parse(dictionary)!.AsObject();
        node[key] = value;
        return node.ToJsonString();
    }

    // A page, object 1, that draws every image from its own resources.
    private string Describe(params (int Number, string Dictionary)[] images)
    {
        var xobjects = new JsonObject();
        foreach (var (number, _) in images)
            xobjects[$"/Im{number}"] = $"{number} 0 R";
        var page = new JsonObject { ["/Type"] = "/Page", ["/Resources"] = new JsonObject { ["/XObject"] = xobjects } }.ToJsonString();
        return Describe([.. images.Select(image => image.Number)], [(1, page)], images);
    }

    // The images pages draw, as qpdf lists them; the page tree and other objects (non-streams) by their dictionaries;
    // and the images, as streams whose data is in files.
    private string Describe(int[] drawn, (int Number, string Dictionary)[] others, (int Number, string Dictionary)[] images)
    {
        var objects = new JsonObject();
        foreach (var (number, dictionary) in others)
            objects[$"obj:{number} 0 R"] = new JsonObject { ["value"] = JsonNode.Parse(dictionary) };
        foreach (var (number, dictionary) in images)
        {
            objects[$"obj:{number} 0 R"] = new JsonObject
            {
                ["stream"] = new JsonObject { ["dict"] = JsonNode.Parse(dictionary), ["datafile"] = $"{Qpdf.ToExtendedPath(Prefix)}-{number}" },
            };
        }

        objects["trailer"] = new JsonObject { ["value"] = new JsonObject { ["/Root"] = "1 0 R" } };
        var description = new JsonObject
        {
            ["pages"] = new JsonArray(new JsonObject
            {
                ["object"] = "1 0 R",
                ["images"] = new JsonArray([.. drawn.Select(number => (JsonNode)new JsonObject { ["object"] = $"{number} 0 R" })]),
            }),
            ["qpdf"] = new JsonArray(new JsonObject { ["jsonversion"] = 2, ["maxobjectid"] = 100 }, objects),
        };
        var path = _dir.Combine("pdf.json");
        File.WriteAllText(path, description.ToJsonString());
        return path;
    }

    private (int Replaced, JsonObject Update) Run(string described, IReadOnlyList<PdfShrunkImage> shrunk, int maxMatches = ImageReplacement.MaxMatches)
    {
        var updatePath = _dir.Combine($"update {Guid.NewGuid():N}.json");
        var replaced = ImageReplacement.WriteUpdate(described, Prefix, shrunk, updatePath, CancellationToken.None, maxMatches);
        var update = JsonNode.Parse(File.ReadAllText(updatePath))!["qpdf"]![1]!.AsObject();
        return (replaced, update);
    }
}
