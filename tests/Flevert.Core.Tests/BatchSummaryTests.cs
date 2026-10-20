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
    public void A_compressed_output_is_named_with_its_size_and_the_original_one()
    {
        // docs/specs/media.md: whoever compresses wants to know it is small enough now.
        var summary = BatchSummary.Describe([Ok("clip.mov", "clip (under 25 MB).mp4") with { Sizes = new SizeChange(412_345_678, 24_301_000) }], 1, LogDir);

        Assert.Equal("Done: clip (under 25 MB).mp4 (24.3 MB, was 412 MB)", summary.Text);
    }

    [Fact]
    public void Several_compressed_outputs_add_up_their_sizes()
    {
        var results = new[]
        {
            Ok("a.mov") with { Sizes = new SizeChange(300_000_000, 40_000_000) },
            Ok("b.mov") with { Sizes = new SizeChange(112_000_000, 21_000_000) },
            Skipped("c.mov"),
        };

        Assert.Equal("Done: 2 files, 412 MB → 61 MB, 1 skipped", BatchSummary.Describe(results, 3, LogDir).Text);
        // Sizes only when every output has them: a count of some would mislead.
        Assert.Equal("Done: 2 files", BatchSummary.Describe([results[0], Ok("d.mov")], 2, LogDir).Text);
    }

    [Theory]
    [InlineData(850_000, "850 KB")]
    [InlineData(999_499, "999 KB")]
    [InlineData(1_000_000, "1 MB")]
    [InlineData(9_870_000, "9.9 MB")]
    [InlineData(24_301_000, "24.3 MB")]
    [InlineData(99_940_000, "99.9 MB")]
    [InlineData(412_345_678, "412 MB")]
    [InlineData(1_234_000_000, "1.2 GB")]
    public void Sizes_are_written_in_megabytes_of_a_million_bytes(long bytes, string text)
    {
        Assert.Equal(text, BatchSummary.SizeText(bytes));
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
    public void A_single_file_that_failed_is_named_with_the_first_line_of_its_reason()
    {
        // Not "Done: 0 files, 1 failed": nothing was done, and the user should see why without opening the log.
        var failed = new FileResult(@"C:\x\scan.pdf", null, $"This PDF is damaged.{Environment.NewLine}System.IO.InvalidDataException: …{Environment.NewLine}   at Op");

        var summary = BatchSummary.Describe([failed], total: 1, LogDir);

        Assert.Equal(@"Failed: scan.pdf: This PDF is damaged. Details: D:\logs", summary.Text);
        Assert.True(summary.HasFailures);
        Assert.True(summary.ShowsLog);
    }

    [Fact]
    public void Several_files_that_all_failed_are_counted_without_done()
    {
        var summary = BatchSummary.Describe([Failed("a"), Failed("b"), Failed("c")], total: 3, LogDir);

        Assert.Equal(@"Failed: 3 files. Details: D:\logs", summary.Text);
        Assert.True(summary.HasFailures);
        Assert.True(summary.ShowsLog);
    }

    [Fact]
    public void Files_that_failed_or_were_skipped_with_none_done_say_failed()
    {
        var summary = BatchSummary.Describe([Failed("a"), Skipped("b")], total: 2, LogDir);

        Assert.Equal(@"Failed: 1 file, 1 skipped. Details: D:\logs", summary.Text);
        Assert.True(summary.HasFailures);
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

    [Fact]
    public void A_joined_output_that_failed_shows_the_first_line_of_the_reason_and_points_to_the_log()
    {
        var outcome = new BatchOutcome([new FileResult(@"C:\x\2.md", null, "2.md: File type .md is not supported by dev.join.\r\n   at Runner")], 1)
        {
            OneOutput = true,
        };

        var summary = BatchSummary.Describe(outcome, LogDir);

        Assert.Equal(@"Failed: 2.md: File type .md is not supported by dev.join. Details: D:\logs", summary.Text);
        Assert.True(summary.HasFailures);
        Assert.True(summary.ShowsLog);
    }

    [Fact]
    public void A_long_reason_for_a_joined_output_is_cut_so_the_notification_keeps_the_log_folder()
    {
        // A notification holds about 255 characters; the reason must not push the log folder out of it.
        var outcome = new BatchOutcome([new FileResult(@"C:\x\a.txt", null, new string('x', 500))], 1) { OneOutput = true };

        var summary = BatchSummary.Describe(outcome, LogDir);

        Assert.True(summary.Text.Length < 200, summary.Text);
        Assert.EndsWith(@"… Details: D:\logs", summary.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_joined_output_says_cancelled()
    {
        var summary = BatchSummary.Describe(new BatchOutcome([], 1) { OneOutput = true }, LogDir);

        Assert.Equal("Cancelled.", summary.Text);
        Assert.False(summary.ShowsLog);
    }

    private static FileResult Ok(string input, string? output = null) => new(input, output ?? input + ".out", null);

    private static FileResult Failed(string input) => new(input, null, "boom");

    private static FileResult Skipped(string input) => new(input, null, "already small", Skipped: true);
}
