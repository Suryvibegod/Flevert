using System.Runtime.CompilerServices;

namespace ConvertAnything.Image.Tests;

internal static class MagickInit
{
    // ImageMagick reads its policy once, at first use. Tests create images directly with Magick.NET, so the policy must be
    // in place before any test runs, whatever order xUnit picks.
    [ModuleInitializer]
    internal static void Initialize() => MagickSetup.EnsureInitialized();
}
