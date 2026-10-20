using Flevert.Core.Menu;

namespace Flevert.Media.Tests;

/// <summary>The menu flevert.exe installs from the real media catalog (docs/specs/media.md, "Menu"; success criterion 9).</summary>
public sealed class MediaMenuTests
{
    private const string Exe = @"C:\Program Files\Flevert\flevert.exe";
    private readonly MemoryRegistry _registry = new();

    public MediaMenuTests() =>
        new MenuInstaller(_registry, Exe, iconExists: _ => true).Install(MediaOperations.All, MediaOperations.Looks);

    [Theory]
    [InlineData(".m4v")]
    [InlineData(".mkv")]
    [InlineData(".avi")]
    [InlineData(".wmv")]
    [InlineData(".3gp")]
    [InlineData(".mts")]
    [InlineData(".m2ts")]
    public void A_video_gets_compress_first_then_convert_extract_audio_and_remove_hidden_info_in_16_entries(string extension)
    {
        // docs/specs/media.md, "Menu": the first user story (too heavy to send) comes first; privacy on its own.
        Assert.Equal(["Compress", "Convert to", "Extract audio", "Remove hidden info"], Labels($@"Flevert.Menu{extension}\shell"));
        Assert.Equal(
            ["Light (best quality)", "Balanced", "Strong (smallest)", "Under 10 MB", "Under 25 MB", "Under 100 MB"],
            Labels($@"Flevert.Menu{extension}\Compress\shell"));
        Assert.Equal(["MP4", "MOV", "WEBM", "GIF"], Labels($@"Flevert.Menu{extension}\Convert to\shell"));
        Assert.Equal(["MP3", "M4A"], Labels($@"Flevert.Menu{extension}\Extract audio\shell"));
        Assert.Equal(16, CountEntries($@"Flevert.Menu{extension}\shell"));
        Assert.Equal(
            [$"\"{Exe}\" run video.strip-metadata \"%1\""],
            _registry.Children($@"Flevert.Menu{extension}\shell").Select(verb => _registry.GetValue($@"{verb}\command", "")).OfType<string>());
    }

    [Theory]
    [InlineData(".mp4", "MP4")]
    [InlineData(".mov", "MOV")]
    [InlineData(".webm", "WEBM")]
    public void A_video_of_a_type_flevert_writes_is_not_offered_its_own_type(string extension, string own)
    {
        var convert = Labels($@"Flevert.Menu{extension}\Convert to\shell");

        Assert.DoesNotContain(own, convert);
        Assert.Equal(3, convert.Length);
        Assert.Equal(15, CountEntries($@"Flevert.Menu{extension}\shell"));
    }

    [Theory]
    [InlineData(".mp3", new[] { "M4A", "WAV", "FLAC", "OGG", "Opus" })]
    [InlineData(".flac", new[] { "MP3", "M4A", "WAV", "OGG", "Opus" })]
    [InlineData(".m4a", new[] { "MP3", "WAV", "FLAC", "OGG", "Opus" })]
    [InlineData(".aac", new[] { "MP3", "M4A", "WAV", "FLAC", "OGG", "Opus" })]
    [InlineData(".wma", new[] { "MP3", "M4A", "WAV", "FLAC", "OGG", "Opus" })]
    public void An_audio_file_gets_convert_to_with_every_other_type(string extension, string[] targets)
    {
        Assert.Equal(["Convert to"], Labels($@"Flevert.Menu{extension}\shell"));
        Assert.Equal(targets, Labels($@"Flevert.Menu{extension}\Convert to\shell"));
        Assert.Equal(
            [.. targets.Select(target => $"\"{Exe}\" run audio.to-{target.ToLowerInvariant()} \"%1\"")],
            _registry.Children($@"Flevert.Menu{extension}\Convert to\shell").Select(verb => _registry.GetValue($@"{verb}\command", "")));
    }

    [Fact]
    public void Every_entry_at_the_top_of_a_media_menu_has_an_icon_and_the_choices_inside_have_none()
    {
        // The menu principles of 1.2.2 (docs/specs/pdf.md, v1.2.2, "Menu"): an icon for each group and single action.
        foreach (var extension in MediaJob.VideoExtensions.Concat(MediaJob.AudioExtensions))
        {
            foreach (var verb in _registry.Children($@"Flevert.Menu{extension}\shell"))
            {
                var label = _registry.GetValue(verb, "MUIVerb");
                Assert.True(_registry.GetValue(verb, "Icon") is { } icon && icon.StartsWith(@"""C:\Program Files\Flevert\icons\", StringComparison.Ordinal),
                    $"{extension}: {label} has no icon");
                if (_registry.GetValue(verb, "ExtendedSubCommandsKey") is { } submenu)
                    Assert.All(_registry.Children($@"{submenu}\shell"), choice => Assert.Null(_registry.GetValue(choice, "Icon")));
            }
        }

        Assert.Equal(@"""C:\Program Files\Flevert\icons\music_note_2.ico"",0", _registry.GetValue(@"Flevert.Menu.mp4\shell\002", "Icon"));
    }

    [Fact]
    public void A_separator_sets_remove_hidden_info_apart_in_the_video_menu_and_the_audio_menu_has_none()
    {
        // ECF_SEPARATORBEFORE; separators do not count toward Explorer's 16 entries (measured with MenuProbe for 1.2.2).
        foreach (var extension in MediaJob.VideoExtensions)
            Assert.Equal(["Remove hidden info"], SeparatedBefore($"Flevert.Menu{extension}"));
        foreach (var extension in MediaJob.AudioExtensions)
            Assert.Empty(SeparatedBefore($"Flevert.Menu{extension}"));
    }

    [Fact]
    public void A_label_pictures_and_pdfs_also_have_looks_the_same_in_the_media_menus()
    {
        // flevert.exe combines these with the looks of the picture and PDF menus (MenuEntryLook.Combine), which refuses
        // a label with two looks: Flevert.Pdf's MenuOperations.Looks has these three.
        Assert.Equal(new MenuEntryLook("arrow_swap", 1), MediaOperations.Looks["Convert to"]);
        Assert.Equal(new MenuEntryLook("arrow_minimize", 1), MediaOperations.Looks["Compress"]);
        Assert.Equal(new MenuEntryLook("shield", 2), MediaOperations.Looks["Remove hidden info"]);
    }

    [Fact]
    public void Every_icon_the_media_menus_name_is_built_beside_flevert()
    {
        // The build copies assets/menu/*.ico into icons\ beside flevert.exe, and the installer packs that folder.
        var icons = Path.Combine(Path.GetDirectoryName(FlevertExeTests.FlevertExe)!, MenuInstaller.IconsFolder);
        foreach (var name in MediaOperations.Looks.Values.Select(look => look.Icon))
            Assert.True(File.Exists(Path.Combine(icons, name + ".ico")), $"{name}.ico is not in assets/menu");
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

    private string[] SeparatedBefore(string menuKey) =>
        [.. _registry.Children($@"{menuKey}\shell")
            .Where(verb => _registry.GetValue(verb, "CommandFlags") == "32")
            .Select(verb => _registry.GetValue(verb, "MUIVerb")!)];

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
    }
}
