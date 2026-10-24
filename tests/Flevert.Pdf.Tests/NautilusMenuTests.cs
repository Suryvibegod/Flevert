using System.Diagnostics;
using System.Text.Json.Nodes;
using Flevert.Core.Menu;

namespace Flevert.Pdf.Tests;

/// <summary>
/// The menu.json install-menu writes from the real catalog, read by the real Nautilus extension (with a stand-in for
/// nautilus-python): what a right-click on a PDF and on a photo shows in Nautilus.
/// </summary>
public sealed class NautilusMenuTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void A_pdf_shows_the_windows_menu_with_the_shift_tools_as_a_submenu_at_the_end()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux only");

        var tree = Show("/home/u/report.pdf");

        JsonNode expected = Menu("Flevert",
            Menu("Compress", "Smaller, same quality", "Smallest, for sharing"), "Merge PDFs", "Split into pages",
            Menu("Convert to", "JPG images", "Text"), Menu("Rotate", "Right 90°", "Left 90°", "180°"), "Add watermark…",
            "Protect with password", "Remove password", "Remove hidden info",
            Menu("More PDF tools", "Remove blank pages", "Add page numbers", Menu("Print layout", "2 pages per sheet", "4 pages per sheet"),
                "Fit pages to A4", "Lock filled forms", Menu("Split by size", "Under 10 MB", "Under 25 MB"), "Convert to PNG images",
                "Extract images"));
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(tree)), tree);
    }

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void A_photo_shows_convert_compress_remove_hidden_info_and_combine_into_pdf()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux only");

        var tree = Show("/home/u/IMG_0001.HEIC");

        var top = JsonNode.Parse(tree)!["Flevert"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(Menu("Convert to", "JPG", "PNG", "WebP", "AVIF", "TIFF"), top[0]), tree);
        Assert.Equal("Compress", ((JsonObject)top[1]!).Single().Key);
        Assert.Contains(top, entry => entry is JsonValue value && (string?)value == "Remove hidden info");
        Assert.Equal("Combine into PDF", (string?)top[^1]);
    }

    // The tree the extension builds for one selected file, as JSON: a label, or {label: children}.
    [SupportedOSPlatform("linux")]
    private string Show(string selected)
    {
        Skip.IfNot(File.Exists("/usr/bin/python3"), "no system Python");
        var flevert = _dir.File("flevert", "#!/bin/sh\n");
        File.SetUnixFileMode(flevert, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // The operations Flevert.Linux installs: those that need tesseract are left out (Windows only so far).
        var operations = MenuOperations.All.Where(op => !MenuOperations.NeedTesseract.Contains(op.Id));
        var menu = _dir.File("menu.json", NautilusMenu.Build(operations, flevert, 1000));
        var extension = Path.Combine(Repository(), "src", "Flevert.Linux", "nautilus");
        var script = $$"""
            import json, sys
            sys.path.insert(0, {{Quote(extension)}})
            import test_flevert_menu as fake
            fake.install_fake_gi("4.0")
            import flevert_menu
            flevert_menu.MENU_FILE = {{Quote(menu)}}
            items = flevert_menu.FlevertMenu().get_file_items([fake.FakeFile({{Quote(selected)}})])
            print(json.dumps([fake.FlevertMenuTests.tree(item) for item in items][0], ensure_ascii=False))
            """;
        var start = new ProcessStartInfo("/usr/bin/python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using var python = Process.Start(start)!;
        var output = python.StandardOutput.ReadToEnd();
        var error = python.StandardError.ReadToEnd();
        python.WaitForExit();
        Assert.True(python.ExitCode == 0, error);
        return output.Trim();
    }

    // {label: [children]}, as the extension's tree prints a submenu.
    private static JsonObject Menu(string label, params JsonNode[] children) => new() { [label] = new JsonArray(children) };

    private static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    private static string Repository() =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
}
