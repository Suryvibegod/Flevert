namespace ConvertAnything.Core.Tests;

public sealed class BatchTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task Single_invocation_leads_and_returns_its_own_files()
    {
        var op = UniqueOp();

        var files = await Batch.CollectAsync(op, [@"C:\a.txt", @"C:\b.txt"], Idle, CancellationToken.None);

        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], files);
    }

    [Fact]
    public async Task Fifty_concurrent_invocations_yield_exactly_one_leader_with_every_file()
    {
        var op = UniqueOp();
        // Each invocation is a separate process in production; enough threads keeps this in-process simulation faithful.
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 120), io);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => Batch.CollectAsync(op, [$@"C:\ảnh {i}.jpg"], Idle, CancellationToken.None))));

        var leader = Assert.Single(results, r => r is not null)!;
        Assert.Equal(50, leader.Count);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $@"C:\ảnh {i}.jpg").Order(), leader.Order());
    }

    [Fact]
    public async Task Different_operations_never_share_a_batch()
    {
        var (opA, opB) = (UniqueOp(), UniqueOp());

        var results = await Task.WhenAll(
            Task.Run(() => Batch.CollectAsync(opA, [@"C:\a.txt"], Idle, CancellationToken.None)),
            Task.Run(() => Batch.CollectAsync(opB, [@"C:\b.txt"], Idle, CancellationToken.None)));

        Assert.Equal([@"C:\a.txt"], results[0]);
        Assert.Equal([@"C:\b.txt"], results[1]);
    }

    [Fact]
    public async Task Invocation_after_batch_closed_starts_a_new_batch()
    {
        var op = UniqueOp();
        await Batch.CollectAsync(op, [@"C:\first.txt"], Idle, CancellationToken.None);

        var second = await Batch.CollectAsync(op, [@"C:\second.txt"], Idle, CancellationToken.None);

        Assert.Equal([@"C:\second.txt"], second);
    }

    private static string UniqueOp() => $"test.{Guid.NewGuid():N}";
}
