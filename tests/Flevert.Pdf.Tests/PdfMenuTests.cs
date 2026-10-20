using Flevert.Core;
using Flevert.Core.Menu;
using Flevert.Image;

namespace Flevert.Pdf.Tests;

/// <summary>The menu flevert.exe installs from the real image and PDF catalogs, in the order flevert.exe lists them.</summary>
public sealed class PdfMenuTests
{
    private const string Exe = @"C:\Program Files\Flevert\flevert.exe";
    private readonly MemoryRegistry _registry = new();

    public PdfMenuTests() =>
        new MenuInstaller(_registry, Exe, iconExists: _ => true).Install(MenuOperations.All, MenuOperations.Looks);

    [Fact]
    public void A_pdf_gets_the_flevert_menu_of_v0_2_2_most_used_first()
    {
        Assert.Equal("Flevert.Menu.pdf", _registry.GetValue(@"SystemFileAssociations\.pdf\shell\Flevert", "ExtendedSubCommandsKey"));
        // docs/specs/pdf.md, v0.2.2, "Menu PDF".
        Assert.Equal(
            ["Compress", "Merge PDFs", "Split into pages", "Convert to", "Rotate", "Protect with password", "Remove password", "Remove hidden info"],
            Labels(@"Flevert.Menu.pdf\shell"));
        Assert.Equal(["pdf.merge", "pdf.split", "pdf.protect", "pdf.unlock", "pdf.strip-metadata"], Commands(@"Flevert.Menu.pdf\shell"));
        Assert.Equal(["Smaller, same quality", "Smallest, for sharing"], Labels(@"Flevert.Menu.pdf\Compress\shell"));
        Assert.Equal(["pdf.compress", "pdf.compress-sharing"], Commands(@"Flevert.Menu.pdf\Compress\shell"));
        Assert.Equal(["JPG images", "PNG images", "Text"], Labels(@"Flevert.Menu.pdf\Convert to\shell"));
        Assert.Equal(["pdf.to-jpg", "pdf.to-png", "pdf.extract-text"], Commands(@"Flevert.Menu.pdf\Convert to\shell"));
        Assert.Equal(["Right 90°", "Left 90°", "180°"], Labels(@"Flevert.Menu.pdf\Rotate\shell"));
        Assert.Equal(["pdf.rotate-right", "pdf.rotate-left", "pdf.rotate-180"], Commands(@"Flevert.Menu.pdf\Rotate\shell"));
        // All 16 entries Explorer shows of one cascade.
        Assert.Equal(16, CountEntries(@"Flevert.Menu.pdf\shell"));
    }

    [Fact]
    public void A_pdf_gets_a_second_menu_flevert_pdf_tools_with_its_own_entries()
    {
        // Explorer's 16 entries are per cascade: the tools have a top-level entry of their own beside Flevert.
        var entry = @"SystemFileAssociations\.pdf\shell\Flevert.Tools";
        Assert.Equal("Flevert PDF tools", _registry.GetValue(entry, "MUIVerb"));
        Assert.Equal("Flevert.Menu.pdf.tools", _registry.GetValue(entry, "ExtendedSubCommandsKey"));
        Assert.Equal("Player", _registry.GetValue(entry, "MultiSelectModel"));
        Assert.Equal(@"""C:\Program Files\Flevert\icons\toolbox.ico"",0", _registry.GetValue(entry, "Icon"));

        Assert.Equal(
            ["Remove blank pages", "Add page numbers", "Add watermark…", "Print layout", "Fit pages to A4", "Flatten forms and comments", "Split by size", "Extract images"],
            Labels(@"Flevert.Menu.pdf.tools\shell"));
        Assert.Equal(
            ["pdf.remove-blank-pages", "pdf.page-numbers", "pdf.watermark", "pdf.fit-a4", "pdf.flatten", "pdf.extract-images"],
            Commands(@"Flevert.Menu.pdf.tools\shell"));
        Assert.Equal(["2 pages per sheet", "4 pages per sheet"], Labels(@"Flevert.Menu.pdf.tools\Print layout\shell"));
        Assert.Equal(["pdf.n-up-2", "pdf.n-up-4"], Commands(@"Flevert.Menu.pdf.tools\Print layout\shell"));
        Assert.Equal(["Under 10 MB", "Under 25 MB"], Labels(@"Flevert.Menu.pdf.tools\Split by size\shell"));
        Assert.Equal(["pdf.split-10mb", "pdf.split-25mb"], Commands(@"Flevert.Menu.pdf.tools\Split by size\shell"));
        Assert.Equal(12, CountEntries(@"Flevert.Menu.pdf.tools\shell"));
    }

    [Fact]
    public void Only_a_pdf_gets_the_second_menu()
    {
        foreach (var extension in ImageFile.ReadableExtensions)
            Assert.Null(_registry.GetValue($@"SystemFileAssociations\{extension}\shell\Flevert.Tools", "MUIVerb"));
    }

    [Fact]
    public void Every_pdf_tool_is_in_a_menu_and_flevert_runs_each_id_once()
    {
        var inMenus = _registry.ValuesUnder("Flevert.Menu.pdf").Concat(_registry.ValuesUnder("Flevert.Menu.pdf.tools")).Where(v => v.StartsWith($"\"{Exe}\" run ", StringComparison.Ordinal)).Select(Id).ToList();
        foreach (var tool in PdfTools.All)
        {
            Assert.Contains(tool.OperationId, inMenus);
            Assert.Single(Catalog, op => op.Id == tool.OperationId);
        }
    }

    [Fact]
    public void Add_page_numbers_from_the_menu_writes_them_at_the_bottom_centre_as_1_of_n()
    {
        Assert.Equal(PdfTools.BottomCenter, PdfToolMenu.PageNumbers[PdfTools.Position]);
        Assert.Equal(PdfTools.OfTotalStyle, PdfToolMenu.PageNumbers[PdfTools.Style]);
        // The choices the tool takes.
        var numbers = PdfTools.All.Single(t => t.OperationId == "pdf.page-numbers");
        Assert.All(PdfToolMenu.PageNumbers, option => Assert.Contains(option.Value, numbers.Options.Single(o => o.Key == option.Key).Choices));
    }

    [Fact]
    public void Every_entry_at_the_top_of_a_menu_has_an_icon_and_the_choices_inside_have_none()
    {
        foreach (var menu in _registry.SubKeyNames("").Where(key => key.StartsWith("Flevert.Menu", StringComparison.Ordinal) && !key.Contains('\\', StringComparison.Ordinal)))
        {
            foreach (var verb in _registry.Children($@"{menu}\shell"))
            {
                var label = _registry.GetValue(verb, "MUIVerb");
                Assert.True(_registry.GetValue(verb, "Icon") is { } icon && icon.StartsWith(@"""C:\Program Files\Flevert\icons\", StringComparison.Ordinal),
                    $"{menu}: {label} has no icon");
                if (_registry.GetValue(verb, "ExtendedSubCommandsKey") is { } submenu)
                    Assert.All(_registry.Children($@"{submenu}\shell"), choice => Assert.Null(_registry.GetValue(choice, "Icon")));
            }
        }
    }

    [Fact]
    public void Every_icon_the_menus_name_is_built_beside_flevert()
    {
        // The build copies assets/menu/*.ico into icons\ beside flevert.exe, and the installer packs that folder.
        var icons = Path.Combine(Path.GetDirectoryName(TestPdf.FlevertExe)!, MenuInstaller.IconsFolder);
        foreach (var name in MenuOperations.Looks.Values.Select(look => look.Icon).Concat(MenuCascade.All.Select(c => c.Icon).OfType<string>()))
            Assert.True(File.Exists(Path.Combine(icons, name + ".ico")), $"{name}.ico is not in assets/menu");
    }

    [Fact]
    public void Separators_group_the_entries_of_each_menu()
    {
        // ECF_SEPARATORBEFORE; separators do not count toward Explorer's 16 entries (measured with MenuProbe).
        Assert.Equal(["Protect with password"], SeparatedBefore("Flevert.Menu.pdf"));
        Assert.Equal(["Print layout", "Extract images"], SeparatedBefore("Flevert.Menu.pdf.tools"));
        Assert.Equal(["Remove hidden info"], SeparatedBefore("Flevert.Menu.jpg"));
        Assert.Equal(["Remove hidden info"], SeparatedBefore("Flevert.Menu.heic"));
    }

    [Fact]
    public void Every_image_type_flevert_reads_gets_combine_into_pdf_and_a_pdf_does_not()
    {
        foreach (var extension in ImageFile.ReadableExtensions)
            Assert.Contains("Combine into PDF", Labels($@"Flevert.Menu{extension}\shell"));
        Assert.DoesNotContain("Combine into PDF", Labels(@"Flevert.Menu.pdf\shell"));
        Assert.DoesNotContain(".pdf", MenuOperations.All.Single(op => op.Id == "pdf.from-images").Extensions);
    }

    [Fact]
    public void No_menu_gets_more_entries_than_explorer_shows()
    {
        // Seen on Windows 11 with MenuProbe: Explorer shows at most 16 entries of a static cascade, submenus and the
        // commands in them all counted, and silently drops the rest (Remove metadata vanished from HEIC when Combine into
        // PDF made its 17th entry). Each top-level entry (Flevert, Flevert PDF tools) has its own 16.
        foreach (var menu in _registry.SubKeyNames("").Where(key => key.StartsWith("Flevert.Menu", StringComparison.Ordinal)))
        {
            var entries = CountEntries($@"{menu}\shell");
            Assert.True(entries <= 16, $"{menu}: {entries} entries");
        }
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".heic")]
    [InlineData(".heif")]
    [InlineData(".webp")]
    [InlineData(".tiff")]
    public void Image_files_get_convert_compress_remove_hidden_info_then_combine_into_pdf_and_no_other_pdf_operation(string extension)
    {
        var commands = _registry.ValuesUnder($"Flevert.Menu{extension}").Where(v => v.StartsWith($"\"{Exe}\" run ", StringComparison.Ordinal)).ToList();

        // docs/specs/pdf.md, v0.2.2, "Menu ảnh": removing GPS before sending a photo comes before joining photos.
        Assert.Equal(["Convert to", "Compress", "Remove hidden info", "Combine into PDF"], Labels($@"Flevert.Menu{extension}\shell"));
        Assert.Equal(
            ["Light (best quality)", "Balanced", "Strong (smallest)", "Under 500 KB", "Under 1 MB", "Under 5 MB"],
            Labels($@"Flevert.Menu{extension}\Compress\shell"));
        Assert.Equal(
            [$"\"{Exe}\" run pdf.from-images \"%1\""],
            commands.Where(command => command.Contains(" run pdf.", StringComparison.Ordinal)));
        Assert.DoesNotContain(ImageOperations.All, op => op.Extensions.Contains(".pdf"));
    }

    [Fact]
    public void A_jpg_converts_to_every_other_format_with_webp_spelled_as_people_write_it_and_heic_has_no_gif()
    {
        Assert.Equal(["PNG", "WebP", "AVIF", "GIF", "BMP", "TIFF"], Labels(@"Flevert.Menu.jpg\Convert to\shell"));
        Assert.Equal(["JPG", "PNG", "WebP", "AVIF", "BMP", "TIFF"], Labels(@"Flevert.Menu.heic\Convert to\shell"));
        Assert.Equal(16, CountEntries(@"Flevert.Menu.jpg\shell"));
        Assert.Equal(16, CountEntries(@"Flevert.Menu.heic\shell"));
    }

    // What flevert.exe can run (Program): the menus' operations and any PDF tool they leave out.
    private static List<IMenuOperation> Catalog =>
        [.. MenuOperations.All, .. PdfTools.Operations.Where(tool => !MenuOperations.All.Any(op => op.Id == tool.Id))];

    private static string Id(string command) => command[$"\"{Exe}\" run ".Length..].Split(' ')[0];

    private string[] Commands(string shellKey) =>
        [.. _registry.Children(shellKey).Select(verb => _registry.GetValue($@"{verb}\command", "")).OfType<string>().Select(Id)];

    private string[] SeparatedBefore(string menuKey) =>
        [.. _registry.Children($@"{menuKey}\shell")
            .Where(verb => _registry.GetValue(verb, "CommandFlags") == "32")
            .Select(verb => _registry.GetValue(verb, "MUIVerb")!)];

    // Every entry below the menu key: each command and each submenu, as Explorer counts them.
    private int CountEntries(string shellKey) => _registry.Children(shellKey).Sum(verb =>
        1 + (_registry.GetValue(verb, "ExtendedSubCommandsKey") is { } submenu ? CountEntries($@"{submenu}\shell") : 0));

    private string[] Labels(string key) =>
        _registry.Children(key).Select(child => _registry.GetValue(child, "MUIVerb")!).ToArray();

    private sealed class MemoryRegistry : IRegistry
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public void SetValue(string key, string name, string value)
        {
            if (!_keys.TryGetValue(key, out var values))
                _keys[key] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            values[name] = value;
        }

        public void SetDword(string key, string name, int value) =>
            SetValue(key, name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public string? GetValue(string key, string name) =>
            _keys.TryGetValue(key, out var values) && values.TryGetValue(name, out var value) ? value : null;

        public void DeleteTree(string key)
        {
            foreach (var k in _keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                _keys.Remove(k);
        }

        public IReadOnlyList<string> SubKeyNames(string key)
        {
            var prefix = key.Length == 0 ? "" : key + "\\";
            return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Full paths of the direct child keys, in the order Explorer lists verbs (key name).</summary>
        public IEnumerable<string> Children(string key) =>
            SubKeyNames(key).Order(StringComparer.OrdinalIgnoreCase).Select(name => $@"{key}\{name}");

        public IEnumerable<string> ValuesUnder(string key) => _keys
            .Where(k => k.Key.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
            .SelectMany(k => k.Value.Values);
    }
}
