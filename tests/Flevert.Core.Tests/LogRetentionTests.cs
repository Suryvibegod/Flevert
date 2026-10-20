namespace Flevert.Core.Tests;

public sealed class LogRetentionTests : IDisposable
{
    private readonly TempDir _logs = new();

    public void Dispose() => _logs.Dispose();

    [Fact]
    public void Prune_deletes_daily_logs_older_than_the_retention_window_only()
    {
        _logs.File("flevert-20261001.log");
        _logs.File("flevert-20261012.log");
        _logs.File("flevert-20261013.log");
        _logs.File("flevert-20261019.log");
        _logs.File("notes.txt");

        new RunLog(_logs.Path).Prune(TimeSpan.FromDays(7), today: new DateOnly(2026, 10, 19));

        Assert.Equal(["flevert-20261013.log", "flevert-20261019.log", "notes.txt"], _logs.FileNames());
    }

    [Fact]
    public void Prune_ignores_a_missing_log_directory()
    {
        new RunLog(_logs.Combine("missing")).Prune(TimeSpan.FromDays(7), today: new DateOnly(2026, 10, 19));
    }
}
