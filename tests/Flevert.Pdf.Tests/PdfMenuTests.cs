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
    public void A_pdf_gets_the_menu_of_v0_2_2_most_used_first_ending_with_more_pdf_tools()
    {
        Assert.Equal("Flevert.Menu.pdf", _registry.GetValue(@"SystemFileAssociations\.pdf\shell\Flevert", "ExtendedSubCommandsKey"));
        // docs/specs/pdf.md, v0.2.2, "Menu PDF".
        Assert.Equal(
            ["Compress", "Merge PDFs", "Split into pages", "Convert to", "Rotate", "Protect with password", "Remove password", "Remove hidden info", "More PDF tools…"],
            Labels(@"Flevert.Menu.pdf\shell"));
        Assert.Equal(
            ["pdf.merge", "pdf.split", "pdf.protect", "pdf.unlock", "pdf.strip-metadata", "pdf.tools"],
            _registry.Children(@"Flevert.Menu.pdf\shell")
                .Select(verb => _registry.GetValue($@"{verb}\command", ""))
                .OfType<string>()
                .Select(Id));
        Assert.Equal(["Smaller, same quality", "Smallest, for sharing"], Labels(@"Flevert.Menu.pdf\Compress\shell"));
        Assert.Equal(["pdf.compress", "pdf.compress-sharing"], Commands(@"Flevert.Menu.pdf\Compress\shell"));
        Assert.Equal(["JPG images", "PNG images"], Labels(@"Flevert.Menu.pdf\Convert to\shell"));
        Assert.Equal(["pdf.to-jpg", "pdf.to-png"], Commands(@"Flevert.Menu.pdf\Convert to\shell"));
        Assert.Equal(["Right 90°", "Left 90°", "180°"], Labels(@"Flevert.Menu.pdf\Rotate\shell"));
        Assert.Equal(["pdf.rotate-right", "pdf.rotate-left", "pdf.rotate-180"], Commands(@"Flevert.Menu.pdf\Rotate\shell"));
        // All 16 entries Explorer shows.
        Assert.Equal(16, CountEntries(@"Flevert.Menu.pdf\shell"));
    }

    [Fact]
    public void Extract_text_and_the_pdf_tools_stay_out_of_the_menu_but_flevert_can_run_them_by_id()
    {
        // docs/specs/pdf.md, v0.2.2: Convert > Text moved into the PDF tools window, under the same id.
        var inMenu = _registry.ValuesUnder("Flevert.Menu.pdf").Where(v => v.StartsWith($"\"{Exe}\" run ", StringComparison.Ordinal)).Select(Id).ToList();
        foreach (var tool in PdfTools.All)
        {
            Assert.DoesNotContain(tool.OperationId, inMenu);
            Assert.Single(MenuOperations.All, op => op.Id == tool.OperationId);
        }

        Assert.Contains("pdf.extract-text", PdfTools.All.Select(t => t.OperationId));
        Assert.Equal(MenuOperations.All.Count, MenuOperations.All.Select(op => op.Id).Distinct().Count());
    }

    [Fact]
    public void Every_entry_at_the_top_of_a_menu_has_an_icon_and_formats_have_none()
    {
        foreach (var extension in MenuOperations.All.SelectMany(op => op.Extensions).Distinct())
        {
            foreach (var verb in _registry.Children($@"Flevert.Menu{extension}\shell"))
            {
                var label = _registry.GetValue(verb, "MUIVerb");
                Assert.True(_registry.GetValue(verb, "Icon") is { } icon && icon.StartsWith(@"""C:\Program Files\Flevert\icons\", StringComparison.Ordinal),
                    $"{extension}: {label} has no icon");
                if (_registry.GetValue(verb, "ExtendedSubCommandsKey") is { } submenu)
                    Assert.All(_registry.Children($@"{submenu}\shell"), format => Assert.Null(_registry.GetValue(format, "Icon")));
            }
        }
    }

    [Fact]
    public void Every_icon_the_menu_and_the_pdf_tools_name_is_built_beside_flevert()
    {
        // The build copies assets/menu/*.ico into icons\ beside flevert.exe, and the installer packs that folder.
        var icons = Path.Combine(Path.GetDirectoryName(TestPdf.FlevertExe)!, MenuInstaller.IconsFolder);
        foreach (var name in MenuOperations.Looks.Values.Select(look => look.Icon).Concat(PdfTools.All.Select(tool => tool.Icon)).Append("document"))
            Assert.True(File.Exists(Path.Combine(icons, name + ".ico")), $"{name}.ico is not in assets/menu");
    }

    [Fact]
    public void Separators_split_the_pdf_menu_into_converting_protecting_and_more_and_the_image_menu_before_remove_hidden_info()
    {
        // ECF_SEPARATORBEFORE; separators do not count toward Explorer's 16 entries (measured with MenuProbe).
        Assert.Equal(["Protect with password", "More PDF tools…"], SeparatedBefore(".pdf"));
        Assert.Equal(["Remove hidden info"], SeparatedBefore(".jpg"));
        Assert.Equal(["Remove hidden info"], SeparatedBefore(".heic"));
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
    public void No_file_type_gets_more_menu_entries_than_explorer_shows()
    {
        // Seen on Windows 11 with MenuProbe: Explorer shows at most 16 entries of a static cascade, submenus and the
        // commands in them all counted, and silently drops the rest (Remove metadata vanished from HEIC when Combine into
        // PDF made its 17th entry).
        foreach (var extension in MenuOperations.All.SelectMany(op => op.Extensions).Distinct())
        {
            var entries = CountEntries($@"Flevert.Menu{extension}\shell");
            Assert.True(entries <= 16, $"{extension}: {entries} entries in the Flevert menu");
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

    private static string Id(string command) => command[$"\"{Exe}\" run ".Length..].Split(' ')[0];

    private string[] Commands(string shellKey) =>
        [.. _registry.Children(shellKey).Select(verb => Id(_registry.GetValue($@"{verb}\command", "")!))];

    private string[] SeparatedBefore(string extension) =>
        [.. _registry.Children($@"Flevert.Menu{extension}\shell")
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
