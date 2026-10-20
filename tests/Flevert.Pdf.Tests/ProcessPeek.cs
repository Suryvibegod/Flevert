using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Pdf.Tests;

/// <summary>
/// Reads what another process was started with, from its own memory (its PEB): the parent's id, the command line and
/// the environment block. What any program of the same user, Task Manager or WMI can see of it.
/// </summary>
internal static unsafe partial class ProcessPeek
{
    private const int ProcessBasicInformation = 0;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    // x64 offsets: PEB.ProcessParameters; RTL_USER_PROCESS_PARAMETERS.CommandLine (UNICODE_STRING), .Environment,
    // .EnvironmentSize.
    private const int PebProcessParameters = 0x20;
    private const int ParametersCommandLine = 0x70;
    private const int ParametersEnvironment = 0x80;
    private const int ParametersEnvironmentSize = 0x3F0;

    public sealed record Started(int ParentId, string CommandLine, string Environment);

    /// <summary>The children of this process named <paramref name="name"/> (no extension) and what they were started with;
    /// a child that exits while being read is left out.</summary>
    public static List<Started> Children(string name)
    {
        var found = new List<Started>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (TryRead(process.Id) is { } started && started.ParentId == Environment.ProcessId)
                    found.Add(started);
            }
        }

        return found;
    }

    public static Started? TryRead(int processId)
    {
        using var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)processId);
        if (handle.IsInvalid)
            return null;
        BasicInformation info;
        if (NtQueryInformationProcess(handle, ProcessBasicInformation, &info, sizeof(BasicInformation), out _) != 0)
            return null;
        if (!TryReadPointer(handle, info.PebBaseAddress + PebProcessParameters, out var parameters)
            || !TryReadValue(handle, parameters + ParametersCommandLine, out ushort commandLineBytes)
            || !TryReadPointer(handle, parameters + ParametersCommandLine + 8, out var commandLineBuffer)
            || !TryReadPointer(handle, parameters + ParametersEnvironment, out var environment)
            || !TryReadPointer(handle, parameters + ParametersEnvironmentSize, out var environmentSize))
            return null;

        var commandLine = new byte[commandLineBytes];
        var block = new byte[Math.Min((long)environmentSize, 1 << 20)];
        if (!TryRead(handle, commandLineBuffer, commandLine) || !TryRead(handle, environment, block))
            return null;
        return new Started((int)info.InheritedFromUniqueProcessId,
            System.Text.Encoding.Unicode.GetString(commandLine), System.Text.Encoding.Unicode.GetString(block));
    }

    private static bool TryReadPointer(SafeProcessHandle handle, nint address, out nint value) => TryReadValue(handle, address, out value);

    private static bool TryReadValue<T>(SafeProcessHandle handle, nint address, out T value) where T : unmanaged
    {
        T read;
        var ok = ReadProcessMemory(handle, address, &read, (nuint)sizeof(T), out var count) && count == (nuint)sizeof(T);
        value = read;
        return ok;
    }

    private static bool TryRead(SafeProcessHandle handle, nint address, byte[] buffer)
    {
        if (buffer.Length == 0)
            return true;
        fixed (byte* p = buffer)
            return ReadProcessMemory(handle, address, p, (nuint)buffer.Length, out var count) && count == (nuint)buffer.Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, void* information, int length, out int returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(SafeProcessHandle process, nint address, void* buffer, nuint size, out nuint read);
}
