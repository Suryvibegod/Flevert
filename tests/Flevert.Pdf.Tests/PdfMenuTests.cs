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
        new MenuInstaller(_registry, Exe).Install(MenuOperations.All);

    [Fact]
    public void A_pdf_gets_the_flevert_menu_with_its_page_conversions_merge_split_rotations_compress_and_metadata_removal()
    {
        Assert.Equal("Flevert.Menu.pdf", _registry.GetValue(@"SystemFileAssociations\.pdf\shell\Flevert", "ExtendedSubCommandsKey"));
        // docs/specs/pdf.md, table of operations: Convert, Merge PDFs, Split into pages, Rotate, Compress, Remove metadata,
        // Protect with password, Remove password.
        Assert.Equal(
            ["Convert", "Merge PDFs", "Split into pages", "Rotate", "Compress", "Remove metadata", "Protect with password", "Remove password"],
            Labels(@"Flevert.Menu.pdf\shell"));
        Assert.Equal(
            [
                $"\"{Exe}\" run pdf.merge \"%1\"", $"\"{Exe}\" run pdf.split \"%1\"", $"\"{Exe}\" run pdf.compress \"%1\"",
                $"\"{Exe}\" run pdf.strip-metadata \"%1\"", $"\"{Exe}\" run pdf.protect \"%1\"", $"\"{Exe}\" run pdf.unlock \"%1\"",
            ],
            _registry.Children(@"Flevert.Menu.pdf\shell")
                .Select(verb => _registry.GetValue($@"{verb}\command", ""))
                .OfType<string>());
        Assert.Equal(["JPG pages", "PNG pages"], Labels(@"Flevert.Menu.pdf\Convert\shell"));
        Assert.Equal(["90° right", "90° left", "180°"], Labels(@"Flevert.Menu.pdf\Rotate\shell"));
        Assert.Equal(
            ["pdf.rotate-right", "pdf.rotate-left", "pdf.rotate-180"],
            _registry.Children(@"Flevert.Menu.pdf\Rotate\shell")
                .Select(verb => _registry.GetValue($@"{verb}\command", "")!)
                .Select(command => command[$"\"{Exe}\" run ".Length..].Split(' ')[0]));
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
    public void Image_files_get_combine_into_pdf_between_compress_and_remove_metadata_and_no_other_pdf_operation(string extension)
    {
        var commands = _registry.ValuesUnder($"Flevert.Menu{extension}").Where(v => v.StartsWith($"\"{Exe}\" run ", StringComparison.Ordinal)).ToList();

        // docs/specs/pdf.md, "Thay đổi ở core" item 7.
        Assert.Equal(["Convert", "Compress", "Combine into PDF", "Remove metadata"], Labels($@"Flevert.Menu{extension}\shell"));
        Assert.Equal(
            [$"\"{Exe}\" run pdf.from-images \"%1\""],
            commands.Where(command => command.Contains(" run pdf.", StringComparison.Ordinal)));
        Assert.DoesNotContain(ImageOperations.All, op => op.Extensions.Contains(".pdf"));
    }

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
