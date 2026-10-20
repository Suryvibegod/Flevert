using Flevert.Core;
using Flevert.Core.Menu;
using Flevert.Image;

namespace Flevert.Pdf.Tests;

/// <summary>The menu flevert.exe installs from the real image and PDF catalogs.</summary>
public sealed class PdfMenuTests
{
    private const string Exe = @"C:\Program Files\Flevert\flevert.exe";
    private readonly MemoryRegistry _registry = new();

    public PdfMenuTests() =>
        new MenuInstaller(_registry, Exe).Install([.. ImageOperations.All, .. PdfOperations.All]);

    [Fact]
    public void A_pdf_gets_the_flevert_menu_with_its_page_conversions_and_rotations()
    {
        Assert.Equal("Flevert.Menu.pdf", _registry.GetValue(@"SystemFileAssociations\.pdf\shell\Flevert", "ExtendedSubCommandsKey"));
        Assert.Equal(["Convert", "Rotate"], Labels(@"Flevert.Menu.pdf\shell"));
        Assert.Equal(["JPG pages", "PNG pages"], Labels(@"Flevert.Menu.pdf\Convert\shell"));
        Assert.Equal(["90° right", "90° left", "180°"], Labels(@"Flevert.Menu.pdf\Rotate\shell"));
        Assert.Equal(
            ["pdf.rotate-right", "pdf.rotate-left", "pdf.rotate-180"],
            _registry.Children(@"Flevert.Menu.pdf\Rotate\shell")
                .Select(verb => _registry.GetValue($@"{verb}\command", "")!)
                .Select(command => command[$"\"{Exe}\" run ".Length..].Split(' ')[0]));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".heic")]
    public void Image_files_get_no_pdf_operation(string extension)
    {
        var commands = _registry.ValuesUnder($"Flevert.Menu{extension}").Where(v => v.StartsWith($"\"{Exe}\" run ", StringComparison.Ordinal)).ToList();

        Assert.Contains("Convert", Labels($@"Flevert.Menu{extension}\shell"));
        Assert.DoesNotContain("Rotate", Labels($@"Flevert.Menu{extension}\shell"));
        Assert.NotEmpty(commands);
        Assert.DoesNotContain(commands, command => command.Contains(" run pdf.", StringComparison.Ordinal));
        Assert.DoesNotContain(ImageOperations.All, op => op.Extensions.Contains(".pdf"));
    }

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
