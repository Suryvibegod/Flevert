using System.Runtime.InteropServices;

namespace Flevert.Cli;

internal static partial class ShellNotify
{
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    /// <summary>Tells Explorer that file associations changed so the new menu shows without a restart.</summary>
    public static void AssociationsChanged() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
