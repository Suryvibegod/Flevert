using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Flevert.Core;

/// <summary>
/// Decides whether the process at the other end of a batch pipe may exchange files with us: it must run the same
/// executable, in the same session, at an integrity level no lower than ours (a sandboxed low-integrity process of the
/// same user must not be able to collect or swallow the user's selections).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class PipePeer
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    public static uint ServerProcessId(NamedPipeClientStream client) =>
        GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var pid) ? pid : 0;

    public static uint ClientProcessId(NamedPipeServerStream server) =>
        GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid) ? pid : 0;

    public static bool IsTrusted(uint processId)
    {
        if (processId == 0)
            return false;
        if (processId == Environment.ProcessId)
            return true;

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
            return false;
        try
        {
            // Both sides read the same way: the kernel reports the resolved path, which differs from the launch path
            // (Environment.ProcessPath) when flevert.exe is started through a junction or a subst drive.
            return ImagePath(process) is { } theirImage
                && string.Equals(theirImage, OwnImagePath.Value, StringComparison.OrdinalIgnoreCase)
                && ProcessIdToSessionId(processId, out var theirs) && ProcessIdToSessionId((uint)Environment.ProcessId, out var ours)
                && theirs == ours
                && IntegrityLevel(process) is { } theirLevel && IntegrityLevel(GetCurrentProcess()) is { } ourLevel
                && theirLevel >= ourLevel;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static readonly Lazy<string?> OwnImagePath = new(() => ImagePath(GetCurrentProcess()));

    /// <summary>This process's executable as the kernel reports it (junctions and subst drives resolved), or null.</summary>
    public static string? OwnImage => OwnImagePath.Value;

    private static string? ImagePath(nint process)
    {
        var buffer = new char[32768];
        var length = (uint)buffer.Length;
        return QueryFullProcessImageNameW(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
    }

    /// <summary>The mandatory integrity RID (0x2000 medium, 0x3000 high), or null if it cannot be read.</summary>
    private static uint? IntegrityLevel(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
            return null;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var size);
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                    return null;
                // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES; its first field points at the label SID.
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                return uint.Parse(sid.Value[(sid.Value.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(nint pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] name, ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, nint info, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
