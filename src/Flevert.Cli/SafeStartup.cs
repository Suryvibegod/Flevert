using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]

namespace Flevert.Cli;

internal static partial class SafeStartup
{
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    /// <summary>
    /// Explorer starts verbs with the selected file's folder as the current directory, which may hold planted DLLs or
    /// executables. Load native code only from the application folder and System32, and leave that folder.
    /// </summary>
    public static void Apply()
    {
        // On systems without the API, at least drop the current directory from the DLL search order.
        if (!SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs))
            SetDllDirectoryW("");
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectoryW(string path);
}
