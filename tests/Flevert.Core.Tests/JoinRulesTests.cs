namespace Flevert.Core.Tests;

/// <summary>Who may still join a batch of a joining operation (ADR-0007, part 2), without processes or pipes.</summary>
public sealed class JoinRulesTests
{
    // In the past: a gate ignores a previous close later than the real clock (it went back), so a later T0 would turn
    // its tests on and off with the time of day.
    private static readonly DateTime T0 = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private static ProcessStamp P(uint id, double seconds) => new(id, T0.AddSeconds(seconds));

    private static readonly Func<ProcessStamp, bool> Dead = _ => false;
    private static readonly Func<ProcessStamp, bool> Running = _ => true;

    [Fact]
    public void Without_a_record_every_process_is_on_time()
    {
        Assert.Equal(Arrival.OnTime, JoinRecord.Classify(P(1, 0), null, T0.AddSeconds(5), Running));
    }

    [Fact]
    public void A_process_created_before_the_last_batch_closed_is_late_however_long_after_it_arrives()
    {
        var record = new JoinRecord(Open: false, P(9, 0), LatestClose: T0.AddSeconds(4), []);

        Assert.Equal(Arrival.Late, JoinRecord.Classify(P(1, 3.9), record, T0.AddMinutes(10), Dead));
    }

    [Fact]
    public void A_process_created_after_the_last_batch_closed_is_on_time_even_a_moment_after()
    {
        // A script running two joins one after the other, or the user making a new selection.
        var record = new JoinRecord(Open: false, P(9, 0), LatestClose: T0.AddSeconds(4), []);

        Assert.Equal(Arrival.OnTime, JoinRecord.Classify(P(1, 4.01), record, T0.AddSeconds(4.02), Running));
    }

    [Fact]
    public void A_member_of_the_last_batch_whose_acknowledgement_was_lost_is_told_it_was_delivered()
    {
        var member = P(1, 1);
        var record = new JoinRecord(Open: false, P(9, 0), T0.AddSeconds(4), [P(2, 1), member]);

        Assert.Equal(Arrival.AlreadyDelivered, JoinRecord.Classify(member, record, T0.AddSeconds(9), Dead));
        // The same id reused by a later process is not that member.
        Assert.Equal(Arrival.OnTime, JoinRecord.Classify(P(1, 6), record, T0.AddSeconds(9), Dead));
    }

    [Fact]
    public void While_another_leader_still_collects_a_process_that_got_a_pipe_of_its_own_is_late()
    {
        // The batch split (a rotated key, a lost pipe name): this process belongs to a batch it cannot reach.
        var record = new JoinRecord(Open: true, P(9, 0), LatestClose: null, []);

        Assert.Equal(Arrival.Late, JoinRecord.Classify(P(1, 0.5), record, T0.AddSeconds(2), Running));
    }

    [Fact]
    public void An_open_batch_whose_leader_is_gone_does_not_make_later_selections_late()
    {
        var record = new JoinRecord(Open: true, P(9, 0), LatestClose: T0.AddSeconds(-30), []);

        Assert.Equal(Arrival.OnTime, JoinRecord.Classify(P(1, 86_400), record, T0.AddSeconds(86_401), Dead));
        // Its own record still dates the batch before it.
        Assert.Equal(Arrival.Late, JoinRecord.Classify(P(1, -31), record, T0.AddSeconds(1), Dead));
    }

    [Fact]
    public void A_gate_ignores_a_previous_close_in_the_future()
    {
        // The clock went back: otherwise every sender would be turned away until it caught up.
        // Relative to the real clock, which the gate reads: a fixed date would pass or fail depending on the time of day.
        var now = DateTime.UtcNow;
        var gate = new JoinGate(new ProcessStamp(9, now), previousClose: now.AddHours(1), () => []);

        Assert.True(gate.Admits(new ProcessStamp(1, now.AddSeconds(0.1))));
    }

    [Fact]
    public void A_close_time_in_the_future_is_ignored()
    {
        // The clock went back after the record was written; otherwise every join would be refused until it caught up.
        var record = new JoinRecord(Open: false, P(9, 0), LatestClose: T0.AddMinutes(30), []);

        Assert.Equal(Arrival.OnTime, JoinRecord.Classify(P(1, 10), record, T0.AddSeconds(11), Dead));
    }

    [Fact]
    public void A_record_survives_being_written_and_read_back()
    {
        var record = new JoinRecord(Open: false, P(9, 0), T0.AddSeconds(4.25), [P(1, 0.1), P(4_000_000_000, 0.2)]);

        var back = JoinRecord.Parse(record.Format());

        Assert.NotNull(back);
        Assert.Equal(record.Open, back.Open);
        Assert.Equal(record.Leader, back.Leader);
        Assert.Equal(record.LatestClose, back.LatestClose);
        Assert.Equal(record.Members, back.Members);
    }

    [Theory]
    [InlineData("")]
    [InlineData("flevert-join 2\nclosed\nleader 1 1\n")] // a later version
    [InlineData("flevert-join 1\nshut\nleader 1 1\n")]
    [InlineData("flevert-join 1\nclosed\n")]
    [InlineData("flevert-join 1\nclosed\nleader 1 -5\n")]
    [InlineData("flevert-join 1\nclosed\nleader 1 1\nmember x 1\n")]
    [InlineData("flevert-join 1\nclosed\nleader 1 1\nclose 99999999999999999999\n")]
    public void A_damaged_or_unknown_record_reads_as_none(string text)
    {
        Assert.Null(JoinRecord.Parse(text));
    }

    [Fact]
    public void A_gate_lets_in_the_processes_of_its_own_selection_in_any_order()
    {
        // Explorer created them 0.3 s apart; the slowest to start may be the first created.
        var census = new List<Sighting> { Seen(1, -0.6), Seen(2, -0.3), Seen(3, 0.3), Seen(4, 0.6) };
        var gate = new JoinGate(P(9, 0), previousClose: null, () => census);

        Assert.All(new[] { P(4, 0.6), P(1, -0.6), P(3, 0.3), P(2, -0.3) }, p => Assert.True(gate.Admits(p)));
    }

    [Fact]
    public void A_gate_turns_away_a_selection_made_after_its_own()
    {
        // The user selected more files and chose the same command while this batch was still collecting.
        var census = new List<Sighting> { Seen(1, 0.2), Seen(5, 3.5), Seen(6, 3.6) };
        var gate = new JoinGate(P(9, 0), previousClose: null, () => census);

        Assert.True(gate.Admits(P(1, 0.2)));
        Assert.False(gate.Admits(P(5, 3.5)));
    }

    [Fact]
    public void A_gate_chains_through_processes_it_knows_about_even_if_they_have_not_arrived()
    {
        // Under load the shell took 18.6 s for 100 processes, at most 0.5 s apart.
        var census = Enumerable.Range(1, 40).Select(i => Seen((uint)i, i * 0.5)).ToList();
        var gate = new JoinGate(P(99, 0), previousClose: null, () => census);

        Assert.True(gate.Admits(P(40, 20)));
    }

    [Fact]
    public void A_gate_turns_away_a_process_created_before_the_previous_batch_closed()
    {
        var gate = new JoinGate(P(9, 10), previousClose: T0.AddSeconds(9.5), () => [Seen(1, 9.4)]);

        Assert.False(gate.Admits(P(1, 9.4)));
    }

    [Fact]
    public void A_gate_lets_in_a_sender_it_cannot_identify()
    {
        var gate = new JoinGate(P(9, 0), previousClose: null, () => []);

        Assert.True(gate.Admits(null));
    }

    [Fact]
    public void A_gate_waits_for_a_process_of_its_selection_that_has_not_handed_over_its_file()
    {
        var census = new List<Sighting> { Seen(1, 0.1), Seen(2, 0.2) };
        var gate = new JoinGate(P(9, 0), previousClose: null, () => census);
        gate.Accept(P(1, 0.1));

        Assert.True(gate.ExpectsMore());

        gate.Accept(P(2, 0.2));
        Assert.False(gate.ExpectsMore());
        Assert.Equal([P(1, 0.1), P(2, 0.2)], gate.Members);
    }

    [Fact]
    public void A_gate_does_not_wait_for_another_selection_or_for_late_processes()
    {
        var census = new List<Sighting> { Seen(5, 4), Seen(6, -20) };
        var gate = new JoinGate(P(9, 0), previousClose: T0.AddSeconds(-10), () => census);

        Assert.False(gate.ExpectsMore());
    }

    [Fact]
    public void A_gate_names_the_files_of_processes_that_ended_without_handing_them_over()
    {
        // Killed, out of memory, or Windows could not start it: its file must not silently go missing.
        var census = new List<Sighting> { Seen(1, 0.1, @"C:\ảnh 1.jpg"), Seen(2, 0.2, @"C:\ảnh 2.jpg") };
        var gate = new JoinGate(P(9, 0), previousClose: null, () => census);
        Assert.True(gate.ExpectsMore());
        gate.Accept(P(1, 0.1));
        census.RemoveAt(1); // process 2 died

        Assert.False(gate.ExpectsMore());
        Assert.Equal([@"C:\ảnh 2.jpg"], gate.Missing());
    }

    private static Sighting Seen(uint id, double seconds, string? file = null) =>
        new(P(id, seconds), [file ?? $@"C:\{id}.txt"]);
}
