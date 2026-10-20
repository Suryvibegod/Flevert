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

    /// <summary>It existed before the last batch of its operation closed, or another leader is still collecting: its
    /// file belongs to a batch that can no longer take it.</summary>
    Late,

    /// <summary>The last batch accepted it already; only the acknowledgement was lost.</summary>
    AlreadyDelivered,
}

/// <summary>
/// The last batch of a joining operation, kept beside its key: who leads or led it, the latest moment a batch of the
/// operation closed, and, once closed, who handed it a file. ADR-0007, part 2.
/// </summary>
/// <param name="LatestClose">For a closed batch, when it closed; for an open one, when the batch before it closed.</param>
internal sealed record JoinRecord(bool Open, ProcessStamp Leader, DateTime? LatestClose, IReadOnlyList<ProcessStamp> Members)
{
    private const string Header = "flevert-join 1";

    public static Arrival Classify(ProcessStamp self, JoinRecord? record, DateTime nowUtc, Func<ProcessStamp, bool> isRunning)
    {
        if (record is null)
            return Arrival.OnTime;
        if (record.Members.Contains(self))
            return Arrival.AlreadyDelivered;
        // A close in the future means the clock went back since: it says nothing about this process.
        if (record.LatestClose is { } closed && closed <= nowUtc && self.Created < closed)
            return Arrival.Late;
        return record.Open && record.Leader != self && isRunning(record.Leader) ? Arrival.Late : Arrival.OnTime;
    }

    public string Format()
    {
        var text = new StringBuilder().AppendLine(Header).AppendLine(Open ? "open" : "closed");
        Line(text, "leader", Leader);
        if (LatestClose is { } close)
            text.Append("close ").AppendLine(close.Ticks.ToString(CultureInfo.InvariantCulture));
        foreach (var member in Members)
            Line(text, "member", member);
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
                default:
                    return null;
            }
        }

        return leader is { } l ? new JoinRecord(lines[1] == "open", l, close, members) : null;
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
