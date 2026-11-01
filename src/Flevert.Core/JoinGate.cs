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
/// <param name="clock">The time now, in UTC; tests replace it. Read as the gate is made, which is when the leader's own
/// files are handed over, and at every delivery.</param>
internal sealed class JoinGate(ProcessStamp leader, DateTime? previousClose, Func<IReadOnlyList<Sighting>> census, Func<DateTime>? clock = null)
{
    /// <summary>Largest gap between two creations of one selection. The shell was measured at 0.5 s at most under load.</summary>
    public static readonly TimeSpan ChainGap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Largest gap between two creations of one selection while a process created before the gap has not handed over its
    /// file yet. Processes starting by the dozen starve the one creating them: with 100 on four cores, a pause of 2.7 s
    /// was seen after the 51st, and the 49 created after it were left out. Another selection is made by hand, after the
    /// earlier one has started; while that one is still starting, the two are taken as one.
    /// </summary>
    public static readonly TimeSpan StartingChainGap = TimeSpan.FromSeconds(10);

    /// <summary>How long after the last delivery a process on the chain that has not handed over still holds the batch.</summary>
    public static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(30);

    private static readonly Func<DateTime> RealClock = static () => DateTime.UtcNow;

    private readonly Lock _gate = new();
    private readonly Func<DateTime> _now = clock ?? RealClock;
    private readonly DateTime _leaderDelivered = (clock ?? RealClock)();
    private readonly List<ProcessStamp> _members = [];
    private readonly Dictionary<ProcessStamp, DateTime> _deliveredAt = [];
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

    /// <summary>
    /// Looks at the processes running now; called as the leader starts. A process of the selection that ends before the
    /// gate would otherwise have looked (at the first idle window, or at a sender far from the chain) is then known, and
    /// named missing instead of silently left out of the output.
    /// </summary>
    public void Survey()
    {
        lock (_gate)
            Look();
    }

    public void Accept(ProcessStamp sender)
    {
        lock (_gate)
        {
            if (_deliveredAt.TryAdd(sender, _now()))
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

    /// <summary>
    /// Files of processes seen on the chain that never handed them over, and whether each process is still running (it
    /// was too slow to start) or has ended.
    /// </summary>
    public IReadOnlyList<MissingFile> Missing()
    {
        lock (_gate)
        {
            var absent = Absent();
            if (absent.Count == 0)
                return [];
            var running = census().Select(s => s.Process).ToHashSet();
            return [.. absent.SelectMany(s => s.Value.Select(file => new MissingFile(file, running.Contains(s.Key))))];
        }
    }

    /// <summary>The processes whose files <see cref="Missing"/> names.</summary>
    public IReadOnlyList<ProcessStamp> Absentees()
    {
        lock (_gate)
            return [.. Absent().Select(s => s.Key)];
    }

    private List<KeyValuePair<ProcessStamp, IReadOnlyList<string>>> Absent()
    {
        var (first, last) = Chain();
        return [.. _seen.Where(s => !_members.Contains(s.Key) && s.Key.Created >= first && s.Key.Created <= last).OrderBy(s => s.Key.Created)];
    }

    // A close in the future means the clock went back since: it says nothing, or every sender would be turned away.
    private bool IsBeforePreviousClose(DateTime created) =>
        previousClose is { } closed && closed <= _now() && created < closed;

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

    /// <summary>
    /// The run of creation times around the leader's with no gap wider than <see cref="ChainGap"/>, or than
    /// <see cref="StartingChainGap"/> where a process created before the gap had not handed over its file when it ended.
    /// </summary>
    private (DateTime First, DateTime Last) Chain(DateTime? candidate = null)
    {
        // Every process known, with when it handed over its files: not yet, for those seen running and for the candidate.
        var known = _members.Select(m => (m.Created, Delivered: _deliveredAt[m]))
            .Concat(_seen.Keys.Where(p => !_deliveredAt.ContainsKey(p)).Select(p => (p.Created, Delivered: DateTime.MaxValue)))
            .Append((leader.Created, Delivered: _leaderDelivered));
        if (candidate is { } c)
            known = known.Append((c, Delivered: DateTime.MaxValue));
        var sorted = known.OrderBy(k => k.Created).ToList();

        // For each process, when it and every process created before it had all handed over their files.
        var settled = new DateTime[sorted.Count];
        for (var i = 0; i < sorted.Count; i++)
            settled[i] = i > 0 && settled[i - 1] > sorted[i].Delivered ? settled[i - 1] : sorted[i].Delivered;

        bool Linked(int later)
        {
            var gap = sorted[later].Created - sorted[later - 1].Created;
            return gap <= ChainGap || (gap <= StartingChainGap && settled[later - 1] > sorted[later].Created);
        }

        var at = sorted.FindIndex(k => k.Created == leader.Created);
        var (low, high) = (at, at);
        while (low > 0 && Linked(low))
            low--;
        while (high < sorted.Count - 1 && Linked(high + 1))
            high++;
        return (sorted[low].Created, sorted[high].Created);
    }
}
