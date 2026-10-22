using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core;

/// <summary>
/// The few libc calls the Linux side needs, in one place. Flag values are those of the x86-64 kernel headers (checked
/// against glibc 2.35); some differ on other architectures, so anything else is refused rather than guessed
/// (docs/specs/linux.md: x86-64 only). <c>statx</c> is used instead of <c>stat</c> because its layout is the same on every
/// architecture.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class Libc
{
    public const int OPath = 0x200000;
    public const int OCloexec = 0x80000;
    public const int ORdonly = 0;
    public const int ONonblock = 0x800;
    public const int AtFdCwd = -100;
    public const int AtEmptyPath = 0x1000;
    public const int AtSymlinkNofollow = 0x100;
    public const int AtRemoveDir = 0x200;
    public const uint RenameNoReplace = 1;

    public const uint StatxType = 0x1;
    public const uint StatxMode = 0x2;
    public const uint StatxNlink = 0x4;
    public const uint StatxUid = 0x8;
    public const uint StatxIno = 0x100;
    public const uint StatxBasicStats = 0x7ff;

    // What every check reads; a file system may leave fields out (stx_mask says which), and a field left out is zero.
    private const uint StatxNeeded = StatxType | StatxMode | StatxNlink | StatxUid | StatxIno;

    public const int SIfMt = 0xF000;
    public const int SIfReg = 0x8000;
    public const int SIfDir = 0x4000;
    public const int SIfLnk = 0xA000;

    public const int EExist = 17;
    public const int EIsDir = 21;
    public const int EInval = 22;
    public const int ENoSys = 38;
    public const int ENotEmpty = 39;
    public const int EOpNotSupp = 95;

    private static readonly bool X64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public static int ONofollow => X64 ? 0x20000 : throw Unsupported();

    public static int ODirectory => X64 ? 0x10000 : throw Unsupported();

    private static PlatformNotSupportedException Unsupported() =>
        new($"Flevert for Linux runs on x86-64 only, not {RuntimeInformation.ProcessArchitecture}.");

    /// <summary>The fields of <c>struct statx</c> Flevert reads; the struct is 256 bytes with this layout everywhere.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Nlink;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Ino;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;

        public readonly int Type => Mode & SIfMt;
    }

    /// <summary>Opens <paramref name="path"/>; an invalid handle with the error in <see cref="Marshal.GetLastPInvokeError"/>.</summary>
    public static SafeFileHandle Open(string path, int flags, uint mode = 0)
    {
        var fd = open(path, flags, mode);
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    /// <summary>Reads what the handle is open on, never following a link it may be open on.</summary>
    public static bool TryStat(SafeFileHandle handle, out Statx stat)
    {
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            return Complete(statx((int)handle.DangerousGetHandle(), "", AtEmptyPath | AtSymlinkNofollow, StatxBasicStats, out stat), stat);
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
    }

    /// <summary>
    /// The path the kernel holds for an open handle (every link in the spelling used to open it resolved), or null. A name
    /// that is not valid UTF-8 has no exact .NET spelling, so it is null too.
    /// </summary>
    public static string? PathOf(SafeFileHandle handle)
    {
        var buffer = new byte[4096];
        var length = readlink(HandlePath(handle), buffer, buffer.Length);
        if (length <= 0 || length >= buffer.Length)
            return null;
        try
        {
            var path = StrictUtf8.GetString(buffer, 0, (int)length);
            return path.StartsWith('/') ? path : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public static string ErrorMessage(int errno) => Marshal.GetPInvokeErrorMessage(errno);

    public static uint EffectiveUserId => geteuid();

    /// <summary>mkdir: 0, or -1 with the error in <see cref="Marshal.GetLastPInvokeError"/>. Never follows a link.</summary>
    public static int MakeDirectory(string path, uint mode) => mkdir(path, mode);

    /// <summary>renameat2 between two paths: 0, or -1 with the error. <see cref="RenameNoReplace"/> fails on any name taken.</summary>
    public static int Rename(string from, string to, uint flags) => renameat2(AtFdCwd, from, AtFdCwd, to, flags);

    /// <summary>unlinkat relative to an open folder (or <see cref="AtFdCwd"/> for a path): 0, or -1 with the error.</summary>
    public static int Unlink(SafeFileHandle? folder, string name, int flags)
    {
        if (folder is null)
            return unlinkat(AtFdCwd, name, flags);
        var added = false;
        folder.DangerousAddRef(ref added);
        try
        {
            return unlinkat((int)folder.DangerousGetHandle(), name, flags);
        }
        finally
        {
            if (added)
                folder.DangerousRelease();
        }
    }

    /// <summary>What <paramref name="path"/> names itself: a link is described, never followed.</summary>
    public static bool TryStatNoFollow(string path, out Statx stat) =>
        Complete(statx(AtFdCwd, path, AtSymlinkNofollow, StatxBasicStats, out stat), stat);

    // Fails, as "operation not supported", a description without a field the checks read, rather than let a zero pass for
    // a link count or an owner.
    private static bool Complete(int result, in Statx stat)
    {
        if (result != 0)
            return false;
        if ((stat.Mask & StatxNeeded) == StatxNeeded)
            return true;
        Marshal.SetLastPInvokeError(EOpNotSupp);
        return false;
    }

    /// <summary>
    /// This process's umask, read from /proc (Linux 4.7 and later) rather than with umask(2), which would change it for
    /// every thread while it is read. 022 when /proc does not say.
    /// </summary>
    public static UnixFileMode Umask()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Umask:", StringComparison.Ordinal))
                    return (UnixFileMode)Convert.ToInt32(line["Umask:".Length..].Trim(), 8);
            }
        }
        catch (IOException)
        {
        }

        return UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    }

    /// <summary>
    /// A path that reaches exactly what the handle is open on (the kernel's link for it), for the calls .NET only offers
    /// by path; an <c>O_PATH</c> handle cannot be changed through directly.
    /// </summary>
    public static string HandlePath(SafeFileHandle handle) => $"/proc/self/fd/{handle.DangerousGetHandle()}";

    /// <summary>Sends <paramref name="signal"/> to a process, or to a process group when <paramref name="pid"/> is negative.</summary>
    public static int Kill(int pid, int signal) => kill(pid, signal);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags, uint mode);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, out Statx statx);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport("libc")]
    private static partial uint geteuid();

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int mkdir(string path, uint mode);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int renameat2(int fromDir, string from, int toDir, string to, uint flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int unlinkat(int dir, string name, int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint readlink(string path, byte[] buffer, nint size);
}
