namespace Flevert.Core.Tests;

public sealed class BatchProgressTextTests
{
    [Fact]
    public void Counts_finished_files_not_the_position_of_the_file_just_started()
    {
        // With files running in parallel, the seventh file can start while only two are done.
        Assert.Equal("2 of 10 done: p7.heic", BatchProgressText.Describe(new BatchProgress(2, 10, @"C:\x\p7.heic"), lastStarted: null));
    }

    [Fact]
    public void Keeps_naming_the_last_started_file_when_a_file_finishes()
    {
        Assert.Equal("3 of 10 done: p7.heic", BatchProgressText.Describe(new BatchProgress(3, 10, null), lastStarted: @"C:\x\p7.heic"));
    }

    [Fact]
    public void Shows_only_the_count_before_any_file_started()
    {
        Assert.Equal("0 of 4 done", BatchProgressText.Describe(new BatchProgress(0, 4, null), lastStarted: null));
    }

    [Fact]
    public void Describes_the_progress_inside_the_current_file_on_its_own_line()
    {
        var progress = new BatchProgress(0, 1, @"C:\x\report.pdf", new FileProgress("page", 37, 200));

        Assert.Equal("page 37 of 200", BatchProgressText.DescribeWithin(progress));
        Assert.Equal("0 of 1 done: report.pdf", BatchProgressText.Describe(progress, lastStarted: null));
    }

    [Fact]
    public void Has_no_line_inside_the_file_when_the_operation_reports_none()
    {
        Assert.Null(BatchProgressText.DescribeWithin(new BatchProgress(2, 10, @"C:\x\p7.heic")));
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
