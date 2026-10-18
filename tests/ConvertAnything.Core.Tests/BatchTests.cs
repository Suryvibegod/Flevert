namespace ConvertAnything.Core.Tests;

public sealed class BatchTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task Single_invocation_leads_and_yields_its_own_files()
    {
        await using var leader = await Batch.LeadOrJoinAsync(UniqueOp(), [@"C:\a.txt", @"C:\b.txt"], Idle, CancellationToken.None);

        Assert.NotNull(leader);
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], await DrainAsync(leader));
    }

    [Fact]
    public async Task Fifty_concurrent_invocations_yield_exactly_one_leader_with_every_file()
    {
        var op = UniqueOp();
        ThreadPool.GetMinThreads(out var workers, out var io);
        // Each invocation is a separate process in production; enough threads keeps this in-process simulation faithful.
        ThreadPool.SetMinThreads(Math.Max(workers, 120), io);
        try
        {
            var joins = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
                Task.Run(() => Batch.LeadOrJoinAsync(op, [$@"C:\ảnh {i}.jpg"], Idle, CancellationToken.None))));

            var leader = Assert.Single(joins, j => j is not null)!;
            await using (leader)
                Assert.Equal(Enumerable.Range(0, 50).Select(i => $@"C:\ảnh {i}.jpg").Order(), (await DrainAsync(leader)).Order());
        }
        finally
        {
            ThreadPool.SetMinThreads(workers, io);
        }
    }

    [Fact]
    public async Task File_arriving_while_the_leader_is_still_processing_joins_the_running_batch()
    {
        var op = UniqueOp();
        await using var leader = (await Batch.LeadOrJoinAsync(op, [@"C:\first.txt"], Idle, CancellationToken.None))!;
        var seen = new List<string>();

        await foreach (var file in leader.ReadAllAsync(CancellationToken.None))
        {
            seen.Add(file);
            if (file == @"C:\first.txt")
            {
                // Simulates a slow conversion: the late file arrives well after the idle window.
                await Task.Delay(Idle * 2);
                Assert.Null(await Batch.LeadOrJoinAsync(op, [@"C:\late.txt"], Idle, CancellationToken.None));
            }
        }

        Assert.Equal([@"C:\first.txt", @"C:\late.txt"], seen);
    }

    [Fact]
    public async Task Different_operations_never_share_a_batch()
    {
        var (opA, opB) = (UniqueOp(), UniqueOp());

        await using var a = await Batch.LeadOrJoinAsync(opA, [@"C:\a.txt"], Idle, CancellationToken.None);
        await using var b = await Batch.LeadOrJoinAsync(opB, [@"C:\b.txt"], Idle, CancellationToken.None);

        Assert.Equal([@"C:\a.txt"], await DrainAsync(a!));
        Assert.Equal([@"C:\b.txt"], await DrainAsync(b!));
    }

    [Fact]
    public async Task Invocation_after_a_batch_closed_starts_a_new_batch()
    {
        var op = UniqueOp();
        await using (var first = await Batch.LeadOrJoinAsync(op, [@"C:\first.txt"], Idle, CancellationToken.None))
            await DrainAsync(first!);

        await using var second = await Batch.LeadOrJoinAsync(op, [@"C:\second.txt"], Idle, CancellationToken.None);

        Assert.Equal([@"C:\second.txt"], await DrainAsync(second!));
    }

    [Fact]
    public async Task Paths_survive_the_pipe_byte_for_byte_including_lone_surrogates()
    {
        var op = UniqueOp();
        var odd = "C:\\x\uD800y.txt";
        await using var leader = (await Batch.LeadOrJoinAsync(op, [@"C:\a.txt"], Idle, CancellationToken.None))!;

        Assert.Null(await Batch.LeadOrJoinAsync(op, [odd, ""], Idle, CancellationToken.None));

        Assert.Equal([@"C:\a.txt", odd], await DrainAsync(leader));
    }

    private static async Task<List<string>> DrainAsync(BatchLeader leader)
    {
        var files = new List<string>();
        await foreach (var file in leader.ReadAllAsync(CancellationToken.None))
            files.Add(file);
        return files;
    }

    private static string UniqueOp() => $"test.{Guid.NewGuid():N}";
}
