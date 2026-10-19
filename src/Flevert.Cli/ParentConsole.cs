using System.Runtime.InteropServices;

namespace Flevert.Cli;

/// <summary>flevert.exe is a WinExe so Explorer clicks never flash a console; attach to the caller's console for terminal commands.</summary>
internal static partial class ParentConsole
{
    private const uint AttachParentProcess = unchecked((uint)-1);
    private const int StdOutputHandle = -11;

    public static void Attach()
    {
        // Output redirected to a pipe or file already has a handle; attaching would send it to the console instead.
        if (GetStdHandle(StdOutputHandle) != 0 || !AttachConsole(AttachParentProcess))
            return;

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int handle);
}
