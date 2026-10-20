using System.Diagnostics;

namespace Flevert.Core;

/// <summary>
/// The flevert.exe processes running an operation right now, other than this one: Explorer starts one per selected file,
/// and each is visible here with its file from the moment it is created, before it has started up. Only processes that
/// would pass the pipe's peer check count (same executable, session, integrity not lower), so another program or a
/// sandboxed copy cannot hold a batch open. ADR-0007, part 2.
/// </summary>
internal static class ProcessCensus
{
    // The command line and the peer check never change for a running process; its id may be reused, its stamp not.
    private static readonly Dictionary<ProcessStamp, Sighting?> Known = [];

    public static IReadOnlyList<Sighting> Take(string operationId)
    {
        var name = Path.GetFileNameWithoutExtension(PipePeer.OwnImage ?? Environment.ProcessPath ?? "flevert");
        var found = new List<Sighting>();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            return found;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                var id = (uint)process.Id;
                if (id == Environment.ProcessId || ProcessStamp.Of(id) is not { } stamp)
                    continue;
                lock (Known)
                {
                    if (!Known.TryGetValue(stamp, out var sighting))
                        Known[stamp] = sighting = Identify(stamp, operationId);
                    if (sighting is not null)
                        found.Add(sighting);
                }
            }
        }

        return found;
    }

    private static Sighting? Identify(ProcessStamp stamp, string operationId)
    {
        // "<exe>" run <op-id> <file>...: what Explorer runs for a menu entry (and scripts too).
        if (ProcessFacts.Arguments(stamp.Id) is not [_, "run", var op, .. var files] || op != operationId || files.Length == 0)
        {
            return null;
        }

        return PipePeer.IsTrusted(stamp.Id) && ProcessFacts.CreationTimeUtc(stamp.Id) == stamp.Created ? new Sighting(stamp, files) : null;
    }
}
