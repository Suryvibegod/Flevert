using System.Globalization;
using System.Text;

namespace Flevert.Core;

/// <summary>A process, told apart from a later one that reuses its id by when it was created.</summary>
internal readonly record struct ProcessStamp(uint Id, DateTime Created)
{
    public static ProcessStamp Self { get; } = new(
        (uint)Environment.ProcessId,
        ProcessFacts.CreationTimeUtc((uint)Environment.ProcessId) ?? DateTime.UtcNow);

    public static ProcessStamp? Of(uint processId) =>
        ProcessFacts.CreationTimeUtc(processId) is { } created ? new ProcessStamp(processId, created) : null;

    public bool IsRunning => ProcessFacts.CreationTimeUtc(Id) == Created;
}

/// <summary>Whether a process of a joining operation may still lead or join a batch (ADR-0007, part 2).</summary>
internal enum Arrival
{
    OnTime,

    /// <summary>It existed before the last batch of its operation closed, or belongs to a batch whose leader died while
    /// collecting: its file belongs to a batch that can no longer take it.</summary>
    Late,

    /// <summary>The last batch accepted it already; only the acknowledgement was lost.</summary>
    AlreadyDelivered,

    /// <summary>Its batch already told the user about its file: it named the file as never having reached it, or the
    /// user cancelled the batch. Nothing to join, and nothing more to say (no second notification).</summary>
    AccountedFor,

    /// <summary>The batch's leader still collects; this process got the name only while the leader was replacing its pipe
    /// instance. Give the name back and hand the file to that leader: it is this process's batch.</summary>
    LeaderCollecting,
}

/// <summary>
/// The last batch of a joining operation, kept beside its key: who leads or led it, the latest moment a batch of the
/// operation closed, and, once closed, who handed it a file. ADR-0007, part 2.
/// </summary>
/// <param name="LatestClose">For a closed batch, when it closed; for an open one, when the batch before it closed.</param>
internal sealed record JoinRecord(bool Open, ProcessStamp Leader, DateTime? LatestClose, IReadOnlyList<ProcessStamp> Members)
{
    private const string Header = "flevert-join 1";

    /// <summary>For a closed batch, the processes of its selection it named as never having handed over their file: some
    /// may still be running, and arrive later.</summary>
    public IReadOnlyList<ProcessStamp> Reported { get; init; } = [];

    /// <summary>For a closed batch, whether the user cancelled it: its late files are dropped without a word.</summary>
    public bool Cancelled { get; init; }

    public static Arrival Classify(ProcessStamp self, JoinRecord? record, DateTime nowUtc, Func<ProcessStamp, bool> isRunning)
    {
        if (record is null)
            return Arrival.OnTime;
        if (record.Members.Contains(self))
            return Arrival.AlreadyDelivered;
        if (record.AccountsFor(self, nowUtc))
            return Arrival.AccountedFor;
        // A close in the future means the clock went back since: it says nothing about this process.
        if (record.LatestClose is { } closed && closed <= nowUtc && self.Created < closed)
            return Arrival.Late;
        if (!record.Open || record.Leader == self)
            return Arrival.OnTime;
        // The leader let go of its name for a moment (it was replacing its pipe instance): give the name back and join.
        if (isRunning(record.Leader))
            return Arrival.LeaderCollecting;
        // It died while collecting, so the files already handed to it are lost; a process of its selection joining the
        // rest would write an output that looks complete. A selection made well after the crash is not held back.
        return (self.Created - record.Leader.Created).Duration() <= SelectionSpan ? Arrival.Late : Arrival.OnTime;
    }

    /// <summary>
    /// How far from its leader's creation a process of the same selection can have been created: the shell took 18.6 s
    /// for 100 processes under load.
    /// </summary>
    public static readonly TimeSpan SelectionSpan = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether this closed batch has already told the user about <paramref name="self"/>'s file: it reported the process
    /// missing, or it was cancelled and the process belongs to its selection (created before the close, within
    /// <see cref="SelectionSpan"/> of the leader).
    /// </summary>
    public bool AccountsFor(ProcessStamp self, DateTime nowUtc)
    {
        if (Open)
            return false;
        if (Reported.Contains(self))
            return true;
        return Cancelled && LatestClose is { } closed && closed <= nowUtc && self.Created < closed
            && (self.Created - Leader.Created).Duration() <= SelectionSpan;
    }

    public string Format()
    {
        var text = new StringBuilder().AppendLine(Header).AppendLine(Open ? "open" : "closed");
        Line(text, "leader", Leader);
        if (LatestClose is { } close)
            text.Append("close ").AppendLine(close.Ticks.ToString(CultureInfo.InvariantCulture));
        foreach (var member in Members)
            Line(text, "member", member);
        foreach (var reported in Reported)
            Line(text, "reported", reported);
        if (Cancelled)
            text.AppendLine("cancelled");
        return text.ToString();
    }

    /// <returns>The record, or <c>null</c> for anything else: an unknown version or a damaged file says nothing.</returns>
    public static JoinRecord? Parse(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3 || lines[0] != Header || lines[1] is not ("open" or "closed"))
            return null;

        ProcessStamp? leader = null;
        DateTime? close = null;
        var members = new List<ProcessStamp>();
        var reported = new List<ProcessStamp>();
        var cancelled = false;
        foreach (var line in lines.Skip(2))
        {
            var parts = line.Split(' ');
            switch (parts)
            {
                case ["leader", var id, var ticks] when Stamp(id, ticks) is { } stamp && leader is null:
                    leader = stamp;
                    break;
                case ["close", var ticks] when Ticks(ticks) is { } t && close is null:
                    close = t;
                    break;
                case ["member", var id, var ticks] when Stamp(id, ticks) is { } stamp:
                    members.Add(stamp);
                    break;
                case ["reported", var id, var ticks] when Stamp(id, ticks) is { } stamp:
                    reported.Add(stamp);
                    break;
                case ["cancelled"]:
                    cancelled = true;
                    break;
                default:
                    return null;
            }
        }

        return leader is { } l ? new JoinRecord(lines[1] == "open", l, close, members) { Reported = reported, Cancelled = cancelled } : null;
    }

    private static void Line(StringBuilder text, string name, ProcessStamp stamp) =>
        text.Append(name).Append(' ').Append(stamp.Id.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .AppendLine(stamp.Created.Ticks.ToString(CultureInfo.InvariantCulture));

    private static ProcessStamp? Stamp(string id, string ticks) =>
        uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && Ticks(ticks) is { } created
            ? new ProcessStamp(pid, created)
            : null;

    private static DateTime? Ticks(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc)
            : null;
}
