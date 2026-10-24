namespace Flevert.Core.Tests;

internal static class Repository
{
    public static string Root()
    {
        // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
    }
}
