using System.Text.Json.Nodes;
using Flevert.Core.Menu;

namespace Flevert.Core.Tests;

/// <summary>
/// menu.json, what the Nautilus extension shows: for each extension the tree the Windows menu has for it, in the same
/// order, with the PDF tools Windows shows only with Shift as a submenu at the end (docs/specs/linux.md).
/// </summary>
public sealed class NautilusMenuTests
{
    private const string Flevert = "/home/u/.local/share/Flevert/app/flevert";

    private static readonly IMenuOperation[] Operations =
    [
        new FakeOp("image.to-png", "Convert to/PNG", ".jpg"),
        new FakeOp("image.to-jpg", "Convert to/JPG", ".png"),
        new FakeOp("image.compress", "Compress", ".jpg", ".png"),
        new FakeOp("pdf.merge", "Merge PDFs", ".pdf"),
        new FakeOp("pdf.to-jpg", "Convert to/JPG images", ".pdf"),
        new FakeOp("pdf.flatten", "Lock filled forms", ".pdf") { Cascade = MenuCascade.PdfTools },
        new FakeOp("pdf.by-id-only", "", ".pdf"),
    ];

    [Fact]
    public void Each_extension_gets_the_tree_of_its_own_operations_in_menu_order()
    {
        var menu = JsonNode.Parse(NautilusMenu.Build(Operations, Flevert, maxFiles: 1000))!;

        Assert.Equal(
            """[{"label":"Convert to","children":[{"label":"PNG","op":"image.to-png"}]},{"label":"Compress","op":"image.compress"}]""",
            menu["menus"]![".jpg"]!.ToJsonString());
        Assert.Equal(
            """[{"label":"Merge PDFs","op":"pdf.merge"},{"label":"Convert to","children":[{"label":"JPG images","op":"pdf.to-jpg"}]},"""
            + """{"label":"More PDF tools","children":[{"label":"Lock filled forms","op":"pdf.flatten"}]}]""",
            menu["menus"]![".pdf"]!.ToJsonString());
    }

    [Fact]
    public void It_names_flevert_the_extensions_of_each_operation_and_the_most_files_a_batch_takes()
    {
        var menu = JsonNode.Parse(NautilusMenu.Build(Operations, Flevert, maxFiles: 1000))!;

        Assert.Equal(1, (int)menu["version"]!);
        Assert.Equal(Flevert, (string)menu["flevert"]!);
        Assert.Equal(1000, (int)menu["maxFiles"]!);
        Assert.Equal("""[".jpg",".png"]""", menu["operations"]!["image.compress"]!.ToJsonString());
        Assert.Null(menu["operations"]!["pdf.by-id-only"]);
    }

    [Fact]
    public void Two_operations_at_one_place_are_refused_as_on_windows()
    {
        Assert.Throws<ArgumentException>(() => NautilusMenu.Build(
            [new FakeOp("a.one", "Compress", ".jpg"), new FakeOp("a.two", "Compress", ".jpg")], Flevert, maxFiles: 1000));
    }

    private sealed class FakeOp(string id, string menuPath, params string[] extensions) : IOperation, IMenuPlacement
    {
        public string Id => id;

        public string MenuPath => menuPath;

        public IReadOnlyCollection<string> Extensions => extensions;

        public MenuCascade Cascade { get; init; } = MenuCascade.Main;

        public Task<string> RunAsync(string inputPath, CancellationToken ct) => Task.FromResult(inputPath);
    }
}
