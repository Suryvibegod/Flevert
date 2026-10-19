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
}
