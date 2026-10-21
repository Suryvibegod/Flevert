namespace Flevert.Pdf.Tests;

/// <summary>
/// The watermark is written in a font of the system, not one Flevert ships: Windows' Arial, or on Linux the TrueType font
/// fontconfig gives for Arial with Vietnamese (Liberation Sans or Arimo, made to replace it). docs/specs/linux.md.
/// </summary>
public sealed class SystemFontTests
{
    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void On_linux_the_font_is_a_truetype_file_with_the_vietnamese_letters()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux only");
        Skip.IfNot(File.Exists("/usr/bin/fc-match"), "fontconfig is not installed");

        var path = SystemFont.Path;

        Assert.NotNull(path);
        Assert.EndsWith(".ttf", path, StringComparison.OrdinalIgnoreCase);
        var glyphs = FontGlyphs.Read(File.ReadAllBytes(path));
        Assert.NotNull(glyphs);
        Assert.Empty(glyphs.Missing("Bản sao — Đà Lạt, tiếng Việt: ẫ ỹ ự ơ ư đ"));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void On_windows_the_font_is_arial()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"), SystemFont.Path);
    }
}
