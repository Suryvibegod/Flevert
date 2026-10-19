namespace ConvertAnything.Image.Tests;

/// <summary>SPEC-image, "Ngân sách bộ nhớ khi chạy song song": parallel files share ImageMagick's one memory limit.</summary>
public sealed class MemoryBudgetTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Grants_in_arrival_order_so_a_large_request_is_not_overtaken_by_small_ones()
    {
        var budget = new MemoryBudget(100);
        var a = await budget.AcquireAsync(60, CancellationToken.None);
        var large = budget.AcquireAsync(60, CancellationToken.None);
        var small = budget.AcquireAsync(10, CancellationToken.None);

        await Task.Delay(100);
        Assert.False(large.IsCompleted);
        Assert.False(small.IsCompleted, "the small request jumped the queue");

        a.Dispose();
        (await large.WaitAsync(Wait)).Dispose();
        (await small.WaitAsync(Wait)).Dispose();
    }

    [Fact]
    public async Task A_request_larger_than_the_budget_runs_alone()
    {
        var budget = new MemoryBudget(100);
        var other = await budget.AcquireAsync(1, CancellationToken.None);
        var huge = budget.AcquireAsync(500, CancellationToken.None);

        await Task.Delay(100);
        Assert.False(huge.IsCompleted);

        other.Dispose();
        using var lease = await huge.WaitAsync(Wait);
        var next = budget.AcquireAsync(1, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(next.IsCompleted, "something ran beside a request that needs the whole budget");
        lease.Dispose();
        (await next.WaitAsync(Wait)).Dispose();
    }

    [Fact]
    public async Task A_request_cancelled_while_waiting_holds_nothing()
    {
        var budget = new MemoryBudget(100);
        var a = await budget.AcquireAsync(80, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = budget.AcquireAsync(80, cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Wait));
        a.Dispose();

        (await budget.AcquireAsync(100, CancellationToken.None).WaitAsync(Wait)).Dispose();
    }

    [Fact]
    public async Task Cancelling_the_first_waiter_lets_the_ones_behind_it_through()
    {
        var budget = new MemoryBudget(100);
        var a = await budget.AcquireAsync(50, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var blocked = budget.AcquireAsync(80, cts.Token);
        var behind = budget.AcquireAsync(40, CancellationToken.None);

        cts.Cancel();

        (await behind.WaitAsync(Wait)).Dispose();
        a.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }

    [Fact]
    public async Task Leases_in_use_never_exceed_the_budget()
    {
        var budget = new MemoryBudget(1000);
        long inUse = 0, peak = 0;

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var size = (ulong)(37 * i % 400 + 1);
            using var lease = await budget.AcquireAsync(size, CancellationToken.None);
            var now = Interlocked.Add(ref inUse, (long)size);
            long seen;
            while ((seen = Interlocked.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            await Task.Yield();
            Interlocked.Add(ref inUse, -(long)size);
        }))).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.InRange(peak, 1, 1000);
    }

    [Fact]
    public void Need_is_read_from_the_header_as_pixels_times_four_bytes_times_three()
    {
        var heic = Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic");

        Assert.Equal(320UL * 240 * 4 * 3, MemoryBudget.EstimateNeed(heic));
    }

    [Fact]
    public void A_file_whose_header_cannot_be_read_asks_for_the_whole_budget()
    {
        using var dir = new TempDir();
        var broken = dir.File("broken.jpg", "not an image");

        Assert.Equal(ulong.MaxValue, MemoryBudget.EstimateNeed(broken));
    }

    [Fact]
    public void Every_image_operation_waits_for_its_share_of_the_budget()
    {
        Assert.All(ImageOperations.All, op => Assert.IsType<BudgetedOperation>(op));
    }

    [Fact]
    public async Task Disposing_a_lease_twice_returns_its_share_once()
    {
        var budget = new MemoryBudget(100);
        var lease = await budget.AcquireAsync(60, CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        var a = budget.AcquireAsync(60, CancellationToken.None);
        var b = budget.AcquireAsync(60, CancellationToken.None);
        Assert.True(a.IsCompleted);
        Assert.False(b.IsCompleted);
        (await a).Dispose();
        (await b.WaitAsync(Wait)).Dispose();
    }
}
