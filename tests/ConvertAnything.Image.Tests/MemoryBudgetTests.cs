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
    public void An_optimized_animation_is_charged_for_every_frame_expanded_to_the_canvas()
    {
        // Each frame is a 1x1 patch, but Coalesce expands all 200 to the 1000x1000 canvas (doubt review, H1).
        using var dir = new TempDir();
        var gif = dir.Combine("anim.gif");
        using (var frames = new ImageMagick.MagickImageCollection())
        {
            for (var i = 0; i < 200; i++)
                frames.Add(new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 1, 1) { Page = new ImageMagick.MagickGeometry(i, i, 1000, 1000), AnimationDelay = 1 });
            frames.Write(gif, ImageMagick.MagickFormat.Gif);
        }

        Assert.True(MemoryBudget.EstimateNeed(gif) >= 1000UL * 1000 * 200 * 4, $"estimated {MemoryBudget.EstimateNeed(gif)}");
    }

    [Fact]
    public void An_animated_webp_is_charged_for_every_frame()
    {
        var webp = Path.Combine(AppContext.BaseDirectory, "fixtures", "blended-240x180.webp");
        using var decoded = ImageFile.Read(webp);

        Assert.True(decoded.Count > 1, "control: the fixture is animated");
        Assert.True(MemoryBudget.EstimateNeed(webp) >= 240UL * 180 * 4 * (ulong)decoded.Count);
    }

    [Fact]
    public void Extra_channels_are_charged()
    {
        using var dir = new TempDir();
        var tiff = dir.Combine("cmyka.tif");
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 100, 100))
        {
            image.ColorSpace = ImageMagick.ColorSpace.CMYK;
            image.Alpha(ImageMagick.AlphaAction.Set);
            Assert.Equal(5u, image.ChannelCount);
            image.Write(tiff, ImageMagick.MagickFormat.Tiff);
        }

        // Ping reports CMYK without the alpha channel (4); decoding gives 5. The estimate allows for an alpha channel.
        Assert.Equal(100UL * 100 * 5 * 3, MemoryBudget.EstimateNeed(tiff));
    }

    [Fact]
    public void A_file_large_on_disk_but_small_in_pixels_is_charged_for_its_size()
    {
        // Decoders read the whole file into memory and copy profiles and comments, none of which ImageMagick counts.
        using var dir = new TempDir();
        var png = dir.Combine("padded.png");
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 16, 16))
        {
            image.Comment = new string('x', 4_000_000);
            image.Write(png, ImageMagick.MagickFormat.Png);
        }

        Assert.True(MemoryBudget.EstimateNeed(png) >= 2 * (ulong)new FileInfo(png).Length);
    }

    [Fact]
    public async Task A_request_for_more_than_half_the_budget_runs_alone()
    {
        // ImageMagick's time limit is per image in wall-clock time: a large image slowed down by others could pass it.
        var budget = new MemoryBudget(100);
        using var large = await budget.AcquireAsync(51, CancellationToken.None);
        var small = budget.AcquireAsync(1, CancellationToken.None);

        await Task.Delay(100);
        Assert.False(small.IsCompleted);
        large.Dispose();
        (await small.WaitAsync(Wait)).Dispose();
    }

    [Fact]
    public void A_budget_of_zero_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryBudget(0));
    }

    [Fact]
    public async Task An_operation_that_fails_returns_its_share()
    {
        var budget = new MemoryBudget(MemoryBudget.EstimateNeed(Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic")));
        var failing = new BudgetedOperation(new FailingOp(), budget);
        var heic = Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic");

        await Assert.ThrowsAsync<IOException>(() => failing.RunAsync(heic, CancellationToken.None));

        (await budget.AcquireAsync(1, CancellationToken.None).WaitAsync(Wait)).Dispose();
    }

    private sealed class FailingOp : Core.IOperation
    {
        public string Id => "image.failing";

        public string MenuPath => "Failing";

        public IReadOnlyCollection<string> Extensions { get; } = [".heic"];

        public Task<string> RunAsync(string inputPath, CancellationToken ct) => throw new IOException("broken");
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
