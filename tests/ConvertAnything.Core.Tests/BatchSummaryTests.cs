namespace ConvertAnything.Core.Tests;

public sealed class BatchSummaryTests
{
    [Fact]
    public void Single_success_names_the_output_file()
    {
        var summary = BatchSummary.Describe([Ok("a.jpg", @"C:\x\a.png")], total: 1);

        Assert.Equal("Done: a.png", summary.Text);
        Assert.False(summary.HasFailures);
    }

    [Fact]
    public void All_successes_count_the_files()
    {
        var summary = BatchSummary.Describe([Ok("a"), Ok("b"), Ok("c")], total: 3);

        Assert.Equal("Done: 3 files", summary.Text);
    }

    [Fact]
    public void Failures_are_counted_and_point_to_the_log()
    {
        var summary = BatchSummary.Describe([Ok("a"), Failed("b"), Failed("c")], total: 3);

        Assert.Equal(@"Done: 1 file, 2 failed. Details: %LOCALAPPDATA%\ConvertAnything\logs", summary.Text);
        Assert.True(summary.HasFailures);
    }

    [Fact]
    public void Cancelled_batch_reports_how_far_it_got()
    {
        var summary = BatchSummary.Describe([Ok("a"), Ok("b")], total: 5);

        Assert.Equal("Cancelled after 2 of 5 files.", summary.Text);
    }

    private static FileResult Ok(string input, string? output = null) => new(input, output ?? input + ".out", null);

    private static FileResult Failed(string input) => new(input, null, "boom");
}
