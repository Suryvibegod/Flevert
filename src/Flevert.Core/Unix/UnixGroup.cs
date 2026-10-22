using System.Globalization;
using System.Runtime.Versioning;

namespace Flevert.Core;

/// <summary>
/// What a job object does for <see cref="ExternalTool"/> on Windows, done on Linux with util-linux: the tool runs through
/// <c>setsid</c>, so it leads a process group of its own (setsid execs in place, so the group's id is the tool's process
/// id) and whatever it starts and leaves running is ended with the group; <c>setpriv --pdeathsig</c> ends it when flevert
/// dies; a memory limit is set by <c>prlimit</c>. Each execs the next in place. All are found at fixed paths, never
/// through PATH.
/// </summary>
/// <remarks>
/// Unlike a job, what the tool starts is not ended when flevert dies (only the tool gets the signal), and nothing stops the
/// tool starting other processes (<see cref="ExternalToolLimits.SingleProcess"/> is not enforced):
/// docs/adr/0010-linux-nautilus-port.md.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class UnixGroup
{
    private const int SigKill = 9;

    public static string Setsid => Find("setsid");

    /// <summary>
    /// The arguments of <see cref="Setsid"/> before the tool's own: setpriv, so the tool gets SIGKILL when flevert dies
    /// (as a job's processes do when its last handle closes), then prlimit for a memory limit, then the tool. Each execs
    /// the next in place, and the death signal stays set across exec. Started from <see cref="UnixStarter"/>'s thread.
    /// </summary>
    public static IEnumerable<string> Prefix(string toolPath, ExternalToolLimits? limits)
    {
        List<string> prefix = [Find("setpriv"), "--pdeathsig", "KILL"];
        if (limits?.MaxMemoryBytes is { } maxMemory)
        {
            ArgumentOutOfRangeException.ThrowIfZero(maxMemory, nameof(limits));
            // RLIMIT_DATA counts the private writable memory a process takes, as a job's limit counts committed memory.
            // RLIMIT_AS would count address space only reserved: .NET reserves tens of gigabytes it never uses, and the
            // PDF worker (flevert itself) ran out of address space under 2.5 GB with 30 MB in use.
            prefix.AddRange([Find("prlimit"), "--data=" + maxMemory.ToString(CultureInfo.InvariantCulture), "--"]);
        }

        prefix.Add(toolPath);
        return prefix;
    }

    /// <summary>
    /// Ends every process left in the group the tool led. Nothing to do once the group is empty. Called after .NET reaped
    /// the leader; Linux gives no new process the group's id while any process of the group is left, and a new group with
    /// that id would need a new process given that id and making itself a group leader in between.
    /// </summary>
    public static void Kill(int groupLeader)
    {
        if (groupLeader > 1)
            _ = Libc.Kill(-groupLeader, SigKill);
    }

    /// <summary>
    /// Whether a tool that ran under a memory limit failed for want of memory. Linux tells nobody when an allocation is
    /// refused (a job reports it on Windows): the tool sees it fail and says so (.NET's "Out of memory.", C++'s
    /// bad_alloc, dd's "memory exhausted"). A tool that dies of a signal without saying so is not counted: a kill or a crash
    /// is not a lack of memory. Only asked when a limit was set.
    /// </summary>
    public static bool LooksOutOfMemory(string standardError) =>
        OutOfMemoryWords.Any(word => standardError.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a tool failed after taking at least three quarters of <paramref name="limit"/>: pdfium's allocator aborts
    /// without a word when refused memory, and qpdf may go on with what it could not read as null and end with warnings
    /// (exit code 3) or an error. SIGKILL does not count: it is what a cancel, a timeout or a stalled page sends, from
    /// outside. The job on Windows reports a refusal whatever the exit code; here a success is trusted.
    /// </summary>
    public static bool DiedNearItsLimit(int exitCode, long peakBytes, ulong limit) =>
        exitCode != 0 && exitCode != 128 + SigKill && (ulong)peakBytes >= limit / 4 * 3;

    private static readonly string[] OutOfMemoryWords =
        ["bad_alloc", "out of memory", "cannot allocate memory", "memory exhausted", "OutOfMemoryException"];

    private static readonly string[] Folders = ["/usr/bin/", "/bin/"];

    private static string Find(string name) =>
        Folders.Select(folder => folder + name).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException($"{name} (util-linux) is missing: Flevert needs it to run its PDF tools.", "/usr/bin/" + name);
}
