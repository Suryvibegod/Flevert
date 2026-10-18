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

    [Fact]
    public async Task Client_that_disconnects_without_sending_does_not_end_or_break_the_batch()
    {
        var op = UniqueOp();
        var leader = (await Batch.LeadOrJoinAsync(op, [@"C:\a.txt"], Idle, CancellationToken.None))!;

        using (var rogue = new System.IO.Pipes.NamedPipeClientStream(".", Batch.PipeName(op),
                   System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
            await rogue.ConnectAsync(2000);
        await Task.Delay(100);

        Assert.Null(await Batch.LeadOrJoinAsync(op, [@"C:\b.txt"], Idle, CancellationToken.None));
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], await DrainAsync(leader));
        await leader.DisposeAsync(); // must not throw
    }

    [Fact]
    public async Task Files_beyond_the_batch_limit_are_refused_so_their_sender_works_alone()
    {
        var op = UniqueOp();
        var many = Enumerable.Range(0, Batch.MaxFilesPerBatch - 1).Select(i => $@"C:\f{i}.txt").ToList();
        await using var leader = (await Batch.LeadOrJoinAsync(op, many, Idle, CancellationToken.None))!;

        await using var overflow = await Batch.LeadOrJoinAsync(op, [@"C:\x.txt", @"C:\y.txt"], Idle, CancellationToken.None);

        Assert.NotNull(overflow);
        Assert.Equal([@"C:\x.txt", @"C:\y.txt"], await DrainAsync(overflow));
        Assert.Equal(many.Count, (await DrainAsync(leader)).Count);
    }

    [Fact]
    public void An_empty_or_corrupt_pipe_secret_is_regenerated()
    {
        using var dir = new TempDir();
        var path = dir.Combine("pipe.key");
        File.WriteAllText(path, "");

        var secret = Batch.ReadOrCreateSecret(path);

        Assert.Matches("^[0-9A-F]{32}$", secret);
        Assert.Equal(secret, Batch.ReadOrCreateSecret(path));
    }

    [Fact]
    public void Batch_keys_live_in_a_folder_only_this_user_system_and_admins_can_open()
    {
        using var dir = new TempDir();
        var keys = Path.Combine(dir.Path, "pipes");

        Batch.ReadOrCreateSecret(Path.Combine(keys, "op.key"));

        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var allowed = new[]
        {
            me.User!.Value, "S-1-5-18", "S-1-5-32-544",
        };
        var rules = new DirectoryInfo(keys).GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>();
        Assert.All(rules, r => Assert.Contains(r.IdentityReference.Value, allowed));
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
