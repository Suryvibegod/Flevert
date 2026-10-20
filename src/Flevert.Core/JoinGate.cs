namespace Flevert.Core;

/// <summary>A running flevert.exe of the same operation, with the files on its command line.</summary>
internal sealed record Sighting(ProcessStamp Process, IReadOnlyList<string> Files);

/// <summary>
/// What a batch leader of a joining operation lets in and waits for (ADR-0007, part 2). Explorer creates the processes
/// of one selection one after another, so their creation times form a chain with short gaps. The leader takes files only
/// from processes on its own chain, keeps the batch open while a running process on it has not handed over its file
/// yet, and knows the files of those that never did. Called from the leader's listener and, once it has closed, from the
/// consumer; a lock keeps the two apart.
/// </summary>
/// <param name="previousClose">When the batch before this one closed: processes created earlier belong to that one.</param>
/// <param name="census">The running processes of the same executable, session and operation, other than this one.</param>
internal sealed class JoinGate(ProcessStamp leader, DateTime? previousClose, Func<IReadOnlyList<Sighting>> census)
{
    /// <summary>Largest gap between two creations of one selection. The shell was measured at 0.5 s at most under load.</summary>
    public static readonly TimeSpan ChainGap = TimeSpan.FromSeconds(2);

    /// <summary>How long after the last delivery a process on the chain that has not handed over still holds the batch.</summary>
    public static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly List<ProcessStamp> _members = [];
    private readonly Dictionary<ProcessStamp, IReadOnlyList<string>> _seen = [];

    public IReadOnlyList<ProcessStamp> Members
    {
        get
        {
            lock (_gate)
                return [.. _members];
        }
    }

    /// <param name="sender">The sender, or <c>null</c> when it could not be identified (it is then let in).</param>
    public bool Admits(ProcessStamp? sender)
    {
        if (sender is not { } s)
            return true;
        if (IsBeforePreviousClose(s.Created))
            return false;

        lock (_gate)
        {
            // Most senders sit next to one already known; only a far one is worth a look at every process.
            if (OnChain(s.Created))
                return true;
            Look();
            return OnChain(s.Created);
        }
    }

    public void Accept(ProcessStamp sender)
    {
        lock (_gate)
        {
            if (!_members.Contains(sender))
                _members.Add(sender);
        }
    }

    /// <summary>Whether a running process on the chain has not handed over its file yet.</summary>
    public bool ExpectsMore()
    {
        lock (_gate)
        {
            var running = Look();
            var (first, last) = Chain();
            return running.Any(p => !_members.Contains(p) && p.Created >= first && p.Created <= last);
        }
    }

    /// <summary>Files of processes seen on the chain that never handed them over.</summary>
    public IReadOnlyList<string> Missing()
    {
        lock (_gate)
        {
            var (first, last) = Chain();
            return [.. _seen.Where(s => !_members.Contains(s.Key) && s.Key.Created >= first && s.Key.Created <= last)
                .OrderBy(s => s.Key.Created).SelectMany(s => s.Value)];
        }
    }

    // A close in the future means the clock went back since: it says nothing, or every sender would be turned away.
    private bool IsBeforePreviousClose(DateTime created) =>
        previousClose is { } closed && closed <= DateTime.UtcNow && created < closed;

    /// <returns>The processes running now.</returns>
    private List<ProcessStamp> Look()
    {
        var running = new List<ProcessStamp>();
        foreach (var sighting in census())
        {
            if (sighting.Process == leader || IsBeforePreviousClose(sighting.Process.Created))
                continue;
            running.Add(sighting.Process);
            _seen.TryAdd(sighting.Process, sighting.Files);
        }

        return running;
    }

    private bool OnChain(DateTime created)
    {
        var (first, last) = Chain(created);
        return created >= first && created <= last;
    }

    /// <summary>The run of creation times around the leader's with no gap wider than <see cref="ChainGap"/>.</summary>
    private (DateTime First, DateTime Last) Chain(DateTime? candidate = null)
    {
        var times = _members.Select(m => m.Created).Concat(_seen.Keys.Select(p => p.Created)).Append(leader.Created);
        if (candidate is { } c)
            times = times.Append(c);
        var sorted = times.Order().ToList();
        var at = sorted.IndexOf(leader.Created);
        var (low, high) = (at, at);
        while (low > 0 && sorted[low] - sorted[low - 1] <= ChainGap)
            low--;
        while (high < sorted.Count - 1 && sorted[high + 1] - sorted[high] <= ChainGap)
            high++;
        return (sorted[low], sorted[high]);
    }
}
