using Flevert.Core.Menu;

namespace Flevert.Media.Tests;

/// <summary>The menu flevert.exe installs from the real media catalog (docs/specs/media.md, "Menu"; success criterion 9).</summary>
public sealed class MediaMenuTests
{
    private const string Exe = @"C:\Program Files\Flevert\flevert.exe";
    private readonly MemoryRegistry _registry = new();

    public MediaMenuTests() =>
        new MenuInstaller(_registry, Exe).Install(MediaOperations.All);

    [Theory]
    [InlineData(".m4v")]
    [InlineData(".avi")]
    [InlineData(".wmv")]
    [InlineData(".3gp")]
    [InlineData(".mts")]
    [InlineData(".m2ts")]
    public void A_video_gets_convert_compress_and_extract_audio_in_exactly_16_entries(string extension)
    {
        Assert.Equal(["Convert", "Compress", "Extract audio"], Labels($@"Flevert.Menu{extension}\shell"));
        Assert.Equal(["MP4", "MOV", "MKV", "WEBM", "GIF"], Labels($@"Flevert.Menu{extension}\Convert\shell"));
        Assert.Equal(["Light", "Balanced", "Strong", "Under 10 MB", "Under 25 MB", "Under 100 MB"], Labels($@"Flevert.Menu{extension}\Compress\shell"));
        Assert.Equal(["MP3", "M4A"], Labels($@"Flevert.Menu{extension}\Extract audio\shell"));
        Assert.Equal(16, CountEntries($@"Flevert.Menu{extension}\shell"));
    }

    [Theory]
    [InlineData(".mp4", "MP4")]
    [InlineData(".mov", "MOV")]
    [InlineData(".mkv", "MKV")]
    [InlineData(".webm", "WEBM")]
    public void A_video_of_a_type_flevert_writes_is_not_offered_its_own_type(string extension, string own)
    {
        var convert = Labels($@"Flevert.Menu{extension}\Convert\shell");

        Assert.DoesNotContain(own, convert);
        Assert.Equal(4, convert.Length);
        Assert.Equal(15, CountEntries($@"Flevert.Menu{extension}\shell"));
    }

    [Theory]
    [InlineData(".mp3", new[] { "M4A", "WAV", "FLAC", "OGG", "Opus" })]
    [InlineData(".flac", new[] { "MP3", "M4A", "WAV", "OGG", "Opus" })]
    [InlineData(".m4a", new[] { "MP3", "WAV", "FLAC", "OGG", "Opus" })]
    [InlineData(".aac", new[] { "MP3", "M4A", "WAV", "FLAC", "OGG", "Opus" })]
    [InlineData(".wma", new[] { "MP3", "M4A", "WAV", "FLAC", "OGG", "Opus" })]
    public void An_audio_file_gets_convert_with_every_other_type(string extension, string[] targets)
    {
        Assert.Equal(["Convert"], Labels($@"Flevert.Menu{extension}\shell"));
        Assert.Equal(targets, Labels($@"Flevert.Menu{extension}\Convert\shell"));
        Assert.Equal(
            [.. targets.Select(target => $"\"{Exe}\" run audio.to-{target.ToLowerInvariant()} \"%1\"")],
            _registry.Children($@"Flevert.Menu{extension}\Convert\shell").Select(verb => _registry.GetValue($@"{verb}\command", "")));
    }

    [Fact]
    public void No_file_type_gets_more_menu_entries_than_explorer_shows()
    {
        // Explorer shows at most 16 entries of a static cascade, submenus and the commands in them all counted (PdfMenuTests).
        foreach (var extension in MediaOperations.All.SelectMany(op => op.Extensions).Distinct())
        {
            var entries = CountEntries($@"Flevert.Menu{extension}\shell");
            Assert.True(entries <= 16, $"{extension}: {entries} entries in the Flevert menu");
        }
    }

    [Fact]
    public void Gif_stays_an_image_and_every_source_type_of_the_spec_has_a_menu()
    {
        var extensions = MediaOperations.All.SelectMany(op => op.Extensions).Distinct().Order(StringComparer.Ordinal);

        Assert.Equal(MediaJob.VideoExtensions.Concat(MediaJob.AudioExtensions).Order(StringComparer.Ordinal), extensions);
        Assert.DoesNotContain(".gif", extensions);
        Assert.Null(_registry.GetValue(@"SystemFileAssociations\.gif\shell\Flevert", "ExtendedSubCommandsKey"));
    }

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
    }
}
