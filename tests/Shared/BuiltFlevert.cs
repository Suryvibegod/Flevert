namespace Flevert.Tests;

/// <summary>The flevert the solution built: flevert.exe of Flevert.Cli on Windows, flevert of Flevert.Linux on Linux.</summary>
internal static class BuiltFlevert
{
    public static string Path
    {
        get
        {
            // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            var configuration = output.Parent!.Name;
            var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            var exe = OperatingSystem.IsWindows()
                ? System.IO.Path.Combine(root, "src", "Flevert.Cli", "bin", configuration, output.Name, "flevert.exe")
                : System.IO.Path.Combine(root, "src", "Flevert.Linux", "bin", configuration, output.Name, "flevert");
            Assert.True(File.Exists(exe), $"Build the solution first; missing {exe}");
            return exe;
        }
    }
}
