using System.Globalization;
using System.Runtime.Versioning;

namespace Flevert.Core;

/// <summary>
/// What a job object does for <see cref="ExternalTool"/> on Windows, done on Linux with util-linux: the tool runs through
/// <c>setsid</c>, so it leads a process group of its own (setsid execs in place, so the group's id is the tool's process
/// id) and whatever it starts and leaves running is ended with the group; a memory limit is an address-space limit set by
/// <c>prlimit</c>, which execs the tool in place too. Both are found at fixed paths, never through PATH.
/// </summary>
/// <remarks>
/// Unlike a job, nothing ends the group when flevert itself dies, and nothing stops the tool starting other processes
/// (<see cref="ExternalToolLimits.SingleProcess"/> is not enforced): docs/adr/0010-linux-nautilus-port.md.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class UnixGroup
{
    private const int SigKill = 9;

    public static string Setsid => Find("setsid");

    /// <summary>The arguments of <see cref="Setsid"/> before the tool's own: the tool, behind prlimit for a memory limit.</summary>
    public static IEnumerable<string> Prefix(string toolPath, ExternalToolLimits? limits)
    {
        if (limits?.MaxMemoryBytes is { } maxMemory)
        {
            ArgumentOutOfRangeException.ThrowIfZero(maxMemory, nameof(limits));
            return [Find("prlimit"), "--as=" + maxMemory.ToString(CultureInfo.InvariantCulture), "--", toolPath];
        }

        return [toolPath];
    }

    /// <summary>Ends every process left in the group the tool led. Nothing to do once the group is empty.</summary>
    public static void Kill(int groupLeader)
    {
        if (groupLeader > 1)
            _ = Libc.Kill(-groupLeader, SigKill);
    }

    /// <summary>
    /// Whether a tool that ran under an address-space limit failed for want of memory. Linux tells nobody when an
    /// allocation is refused (a job reports it on Windows): the tool sees it fail and either says so or dies of a
    /// signal (an unhandled bad_alloc aborts, a missing NULL check segfaults). Only asked when a limit was set.
    /// </summary>
    public static bool LooksOutOfMemory(int exitCode, string standardError) =>
        exitCode > 128 // .NET reports a process ended by a signal as 128 + the signal
        || OutOfMemoryWords.Any(word => standardError.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] OutOfMemoryWords =
        ["bad_alloc", "out of memory", "cannot allocate memory", "memory exhausted", "OutOfMemoryException"];

    private static readonly string[] Folders = ["/usr/bin/", "/bin/"];

    private static string Find(string name) =>
        Folders.Select(folder => folder + name).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException($"{name} (util-linux) is missing: Flevert needs it to run its PDF tools.", "/usr/bin/" + name);
}
