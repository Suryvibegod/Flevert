namespace Flevert.Core.Tests;

public sealed class BatchProgressTextTests
{
    [Fact]
    public void Counts_finished_files_not_the_position_of_the_file_just_started()
    {
        // With files running in parallel, the seventh file can start while only two are done.
        Assert.Equal("2 of 10 done: p7.heic", BatchProgressText.Describe(new BatchProgress(2, 10, "C:/x/p7.heic"), lastStarted: null));
    }

    [Fact]
    public void Keeps_naming_the_last_started_file_when_a_file_finishes()
    {
        Assert.Equal("3 of 10 done: p7.heic", BatchProgressText.Describe(new BatchProgress(3, 10, null), lastStarted: "C:/x/p7.heic"));
    }

    [Fact]
    public void Shows_only_the_count_before_any_file_started()
    {
        Assert.Equal("0 of 4 done", BatchProgressText.Describe(new BatchProgress(0, 4, null), lastStarted: null));
    }

    [Fact]
    public void Shows_what_the_batch_is_waiting_for_instead_of_a_count()
    {
        var waiting = new BatchProgress(0, 1, null, Status: "Waiting for the selected files…");

        Assert.Equal("Waiting for the selected files…", BatchProgressText.Describe(waiting, lastStarted: null));
        Assert.Null(BatchProgressText.DescribeWithin(waiting));
        Assert.Equal((0, 1_000), BatchProgressText.Bar(waiting));
    }

    [Fact]
    public void Describes_the_progress_inside_the_current_file_on_its_own_line()
    {
        var progress = new BatchProgress(0, 1, "C:/x/report.pdf", new FileProgress("page", 37, 200));

        Assert.Equal("page 37 of 200", BatchProgressText.DescribeWithin(progress));
        Assert.Equal("0 of 1 done: report.pdf", BatchProgressText.Describe(progress, lastStarted: null));
    }

    [Fact]
    public void Work_without_countable_parts_is_shown_as_a_percentage_and_moves_the_bar_by_it()
    {
        // A video encodes as one long stream (docs/specs/media.md): "37%", not "% 37 of 100".
        var progress = new BatchProgress(1, 20, @"C:\x\clip.mov", FileProgress.Percent(37));

        Assert.Equal("37%", BatchProgressText.DescribeWithin(progress));
        Assert.Equal((1_370, 20_000), BatchProgressText.Bar(progress));
        Assert.Equal((0, 1_000), BatchProgressText.Bar(new BatchProgress(0, 1, @"C:\x\clip.mov", FileProgress.Percent(0))));
        Assert.Equal((1_000, 1_000), BatchProgressText.Bar(new BatchProgress(0, 1, @"C:\x\clip.mov", FileProgress.Percent(100))));
        Assert.Equal("0%", BatchProgressText.DescribeWithin(new BatchProgress(0, 1, null, FileProgress.Percent(-5))));
        Assert.Equal("100%", BatchProgressText.DescribeWithin(new BatchProgress(0, 1, null, FileProgress.Percent(140))));
    }

    [Fact]
    public void Has_no_line_inside_the_file_when_the_operation_reports_none()
    {
        Assert.Null(BatchProgressText.DescribeWithin(new BatchProgress(2, 10, "C:/x/p7.heic")));
    }

    [Fact]
    public void The_bar_moves_inside_a_single_file_as_its_pages_go_by()
    {
        // One PDF of 4 pages: page N has started, so N - 1 are done (review CR nit: the bar stood at 0 until the end).
        Assert.Equal((0, 1_000), BatchProgressText.Bar(new BatchProgress(0, 1, "C:/x/a.pdf")));
        Assert.Equal((0, 1_000), BatchProgressText.Bar(new BatchProgress(0, 1, "C:/x/a.pdf", new FileProgress("page", 1, 4))));
        Assert.Equal((500, 1_000), BatchProgressText.Bar(new BatchProgress(0, 1, "C:/x/a.pdf", new FileProgress("page", 3, 4))));
        Assert.Equal((1_000, 1_000), BatchProgressText.Bar(new BatchProgress(1, 1, null)));
    }

    [Fact]
    public void The_bar_counts_finished_files_and_adds_the_part_of_the_current_one()
    {
        Assert.Equal((3_000, 10_000), BatchProgressText.Bar(new BatchProgress(3, 10, "C:/x/p7.heic")));
        Assert.Equal((3_250, 10_000), BatchProgressText.Bar(new BatchProgress(3, 10, "C:/x/d.pdf", new FileProgress("page", 2, 4))));
        // Before anything is known the bar is empty, never past its end.
        Assert.Equal((0, 1_000), BatchProgressText.Bar(new BatchProgress(0, 0, null)));
        Assert.Equal((2_000, 2_000), BatchProgressText.Bar(new BatchProgress(2, 2, "C:/x/late.pdf", new FileProgress("page", 9, 9))));
    }

    [Theory]
    [InlineData("page", 0, 200)] // positions count from 1: "page 0 of 200" means nothing
    [InlineData("page", 201, 200)]
    [InlineData("page", 1, 0)]
    [InlineData(" ", 1, 1)]
    public void File_progress_refuses_values_that_would_read_as_nonsense(string unit, int position, int total)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FileProgress(unit, position, total));
    }
}
