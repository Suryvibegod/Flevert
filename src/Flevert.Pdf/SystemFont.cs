using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// The TrueType font the watermark is written in. Flevert ships no font: on Windows it is Windows' Arial, which has the
/// Vietnamese letters; on Linux the font fontconfig gives for Arial with Vietnamese, which on most desktops is one made to
/// replace it with the same metrics (Liberation Sans, Arimo). Only a .ttf file is taken: pdfium embeds TrueType and
/// <see cref="FontGlyphs"/> reads its character map. docs/specs/linux.md.
/// </summary>
internal static class SystemFont
{
    // Found once per process; fontconfig answers in a few milliseconds.
    private static readonly Lazy<string?> LazyPath = new(Find);

    private const string FcMatch = "/usr/bin/fc-match";

    /// <summary>The font file, or <c>null</c> when this system has none to offer (it may still be missing on Windows).</summary>
    public static string? Path => LazyPath.Value;

    private static string? Find()
    {
        if (OperatingSystem.IsWindows())
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(FcMatch))
            return null;

        try
        {
            // -s lists every font in the order fontconfig prefers them, so a first choice that is not TrueType (an .otf or
            // a .ttc collection) still leaves the next one.
            var run = ExternalTool.RunAsync(FcMatch, ["-s", "-f", "%{file}\n", "Arial:lang=vi:fontformat=TrueType"], TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            return run.ExitCode != 0
                ? null
                : run.StandardOutput.Split('\n').FirstOrDefault(file =>
                    file.StartsWith('/') && file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && File.Exists(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
