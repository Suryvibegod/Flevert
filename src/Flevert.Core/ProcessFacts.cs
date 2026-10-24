using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Flevert.Core;

/// <summary>
/// What the batch leader can learn about another running process of this user: when it was created and its command line.
/// Both are set by the creator before the process runs any code, so a process Explorer has just started is already
/// visible, however long it then takes to start up.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ProcessFacts
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x102;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaxCommandLineBytes = 64 * 1024;

    /// <returns>When the process was created, or <c>null</c> if it is not running or cannot be queried.</returns>
    public static DateTime? CreationTimeUtc(uint processId) => WithProcess(processId, CreationTimeUtc);

    /// <returns>The command line split the way Windows programs split it (argument 0 is the program), or <c>null</c> if
    /// the process is not running or cannot be queried.</returns>
    public static string[]? Arguments(uint processId) => WithProcess(processId, process =>
        CommandLine(process) is { } line ? Split(line) : null);

    /// <returns>Both facts of one process, read through one handle, or <c>null</c>.</returns>
    public static (DateTime Created, string[] Arguments)? Read(uint processId) => WithProcess(processId, process =>
        CreationTimeUtc(process) is { } created && CommandLine(process) is { } line && Split(line) is { } args
            ? (created, args)
            : ((DateTime, string[])?)null);

    internal static DateTime? CreationTimeUtc(nint process) =>
        GetProcessTimes(process, out var created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created) : null;

    private static T? WithProcess<T>(uint processId, Func<nint, T?> read)
    {
        if (processId == 0)
            return default;
        var process = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, processId);
        if (process == 0)
            return default;
        try
        {
            // A handle someone else still holds keeps an exited process's object alive; it is not running.
            return WaitForSingleObject(process, 0) == WaitTimeout ? read(process) : default;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string? CommandLine(nint process)
    {
        var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, 0, 0, out var size);
        if (status != StatusInfoLengthMismatch || size is 0 or > MaxCommandLineBytes)
            return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out _) != 0)
                return null;
            // UNICODE_STRING { ushort Length; ushort MaximumLength; PWSTR Buffer }, the text following it in the buffer.
            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == 0 ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string[]? Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == 0)
            return null;
        try
        {
            var args = new string[count];
            for (var i = 0; i < count; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, nint info, uint length, out uint returnLength);

    [LibraryImport("shell32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgvW(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
