using Flevert.Core;

namespace Flevert.Ocr.Tests;

/// <summary>
/// The process-wide gate on tesseract runs (pictures in a batch and pages of a PDF share it) and the OSD answers that are
/// trusted.
/// </summary>
public sealed class TesseractLimitsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static Tesseract Engine(RunTool run) =>
        new(Tesseract.NextToApp.ExecutablePath, Tesseract.NextToApp.TessDataPath, run);

    private static ExternalToolResult Ok(string output) => new(0, output, "", false, false);

    [Fact]
    public async Task No_more_than_MaxParallel_tesseract_runs_at_once_across_engines()
    {
        var image = TestHelpers.CreateTextImage(_dir.Combine("page.png"), "Hello", fontSize: 16);
        var total = Tesseract.MaxParallel * 2 + 1;
        var running = 0;
        var most = 0;
        var allIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<ExternalToolResult> Run(string path, IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            if (now == total)
                allIn.TrySetResult();
            // Without a gate every call gets here at once and they all leave together; with it, a few at a time.
            await Task.WhenAny(allIn.Task, Task.Delay(TimeSpan.FromMilliseconds(300), ct)).ConfigureAwait(false);
            Interlocked.Decrement(ref running);
            return Ok("Rotate: 0\nOrientation confidence: 20\n");
        }

        // Two engines, as an image batch and a PDF in the same flevert.exe would have: the gate is the process's.
        var engines = new[] { Engine(Run), Engine(Run) };
        var calls = Enumerable.Range(0, total).Select(i => Task.Run(() => (i % 3) switch
        {
            0 => engines[i % 2].ReadTextAsync(image, CancellationToken.None),
            1 => engines[i % 2].DetectRotationAsync(image, CancellationToken.None).ContinueWith(t => t.Result == 0 ? "" : "turned", TaskScheduler.Default),
            _ => engines[i % 2].WriteTextLayerAsync(image, _dir.Combine($"layer{i}"), CancellationToken.None)
                .ContinueWith(t => { t.GetAwaiter().GetResult(); return ""; }, TaskScheduler.Default),
        }));

        // The fake writes no PDF, so the text-layer calls fail after their run; only the runs at once matter here.
        await Task.WhenAll(calls.Select(c => c.ContinueWith(_ => { }, TaskScheduler.Default)));

        Assert.InRange(most, 1, Tesseract.MaxParallel);
    }

    [Fact]
    public async Task A_run_waiting_for_the_gate_can_be_cancelled_and_never_starts()
    {
        var image = TestHelpers.CreateTextImage(_dir.Combine("page.png"), "Hello", fontSize: 16);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var allHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<ExternalToolResult> Hold(string path, IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            if (Interlocked.Increment(ref started) == Tesseract.MaxParallel)
                allHeld.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return Ok("text");
        }

        var lateStarted = false;
        Task<ExternalToolResult> Late(string path, IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            lateStarted = true;
            return Task.FromResult(Ok("text"));
        }

        var holder = Engine(Hold);
        var holders = Enumerable.Range(0, Tesseract.MaxParallel)
            .Select(_ => Task.Run(() => holder.ReadTextAsync(image, CancellationToken.None)))
            .ToArray();
        try
        {
            // Other test classes may hold a slot for a moment; wait until this test holds every one.
            await allHeld.Task.WaitAsync(TimeSpan.FromSeconds(60));

            using var cts = new CancellationTokenSource();
            var late = Engine(Late).ReadTextAsync(image, cts.Token);
            await Task.Delay(200);
            Assert.False(lateStarted, "A run must wait while MaxParallel others are running.");
            Assert.False(late.IsCompleted);

            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(lateStarted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(holders);
        }

        // The cancelled wait gave back nothing it did not take: every slot is free again.
        var after = await Engine(Late).ReadTextAsync(image, CancellationToken.None);
        Assert.Equal("text", after);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    [InlineData(45, 0)]
    [InlineData(-90, 0)]
    [InlineData(360, 0)]
    [InlineData(450, 0)]
    [InlineData(12345, 0)]
    public void Only_quarter_turns_are_taken_from_OSD(int rotate, int expected)
    {
        var output = $"Page number: 0\nOrientation in degrees: 0\nRotate: {rotate}\nOrientation confidence: 25.00\n";

        Assert.Equal(expected, Tesseract.ParseOsdRotation(output));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
                return;
            current = seen;
        }
    }
}
