namespace Flevert.Core.Tests;

public sealed class BatchSummaryTests
{
    private const string LogDir = @"D:\logs";

    [Fact]
    public void Single_success_names_the_output_file()
    {
        var summary = BatchSummary.Describe([Ok("a.jpg", @"C:\x\a.png")], total: 1, LogDir);

        Assert.Equal("Done: a.png", summary.Text);
        Assert.False(summary.HasFailures);
    }

    [Fact]
    public void All_successes_count_the_files()
    {
        var summary = BatchSummary.Describe([Ok("a"), Ok("b"), Ok("c")], total: 3, LogDir);

        Assert.Equal("Done: 3 files", summary.Text);
    }

    [Fact]
    public void Failures_are_counted_and_point_to_the_log()
    {
        var summary = BatchSummary.Describe([Ok("a"), Failed("b"), Failed("c")], total: 3, LogDir);

        Assert.Equal(@"Done: 1 file, 2 failed. Details: D:\logs", summary.Text);
        Assert.True(summary.HasFailures);
    }

    [Fact]
    public void Skipped_files_are_counted_apart_and_are_not_failures()
    {
        // e.g. 30 chat photos already compressed: nothing went wrong, there was just nothing to gain.
        var summary = BatchSummary.Describe([Ok("a"), Skipped("b"), Skipped("c")], total: 3, LogDir);

        Assert.Equal("Done: 1 file, 2 skipped", summary.Text);
        Assert.False(summary.HasFailures);
    }

    [Fact]
    public void A_single_skipped_file_says_why()
    {
        var summary = BatchSummary.Describe([Skipped("b")], total: 1, LogDir);

        Assert.Equal("Skipped: already small", summary.Text);
        Assert.False(summary.HasFailures);
    }

    [Fact]
    public void Failures_and_skips_are_both_reported()
    {
        var summary = BatchSummary.Describe([Ok("a"), Skipped("b"), Failed("c")], total: 3, LogDir);

        Assert.Equal(@"Done: 1 file, 1 skipped, 1 failed. Details: D:\logs", summary.Text);
        Assert.True(summary.HasFailures);
    }

    [Fact]
    public void A_batch_where_every_file_was_skipped_for_one_reason_says_it()
    {
        var summary = BatchSummary.Describe([Skipped("a"), Skipped("b"), Skipped("c")], total: 3, LogDir);

        Assert.Equal("Nothing to do: 3 files skipped (already small)", summary.Text);
        Assert.False(summary.HasFailures);
        Assert.False(summary.ShowsLog);
    }

    [Fact]
    public void A_batch_skipped_for_different_reasons_points_to_the_log()
    {
        var summary = BatchSummary.Describe([Skipped("a"), new FileResult("b", null, "other reason", Skipped: true)], total: 2, LogDir);

        Assert.Equal(@"Nothing to do: 2 files skipped. Details: D:\logs", summary.Text);
        Assert.False(summary.HasFailures);
        Assert.True(summary.ShowsLog);
    }

    [Fact]
    public void Skips_do_not_fail_a_batch_but_failures_and_cancellation_do()
    {
        Assert.True(new BatchOutcome([Ok("a"), Skipped("b")], Total: 2).NoFailures);
        Assert.False(new BatchOutcome([Ok("a"), Failed("b")], Total: 2).NoFailures);
        Assert.False(new BatchOutcome([Ok("a")], Total: 2).NoFailures);
    }

    [Fact]
    public void A_skipped_result_never_counts_as_succeeded()
    {
        Assert.False(new FileResult("a", null, null, Skipped: true).Succeeded);
        Assert.Equal("Nothing to gain.", new OperationSkippedException().Message);
    }

    [Fact]
    public void A_cancelled_batch_with_failures_still_points_to_the_log()
    {
        var summary = BatchSummary.Describe([Ok("a"), Failed("b")], total: 5, LogDir);

        Assert.Equal(@"Cancelled after 2 of 5 files, 1 failed. Details: D:\logs", summary.Text);
        Assert.True(summary.HasFailures);
        Assert.True(summary.ShowsLog);
    }

    [Fact]
    public void Cancelled_batch_reports_how_far_it_got()
    {
        var summary = BatchSummary.Describe([Ok("a"), Ok("b")], total: 5, LogDir);

        Assert.Equal("Cancelled after 2 of 5 files.", summary.Text);
    }

    private static FileResult Ok(string input, string? output = null) => new(input, output ?? input + ".out", null);

    private static FileResult Failed(string input) => new(input, null, "boom");

    private static FileResult Skipped(string input) => new(input, null, "already small", Skipped: true);
}
