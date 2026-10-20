using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary>
/// What the PDF worker inherits from flevert.exe (ADR-0008, "Reading untrusted PDFs in a worker"). In the worker
/// collection, so the worker found is this test's; the variables it sets are harmless to anything else running meanwhile.
/// </summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfWorkerEnvironmentTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task The_worker_inherits_no_imagemagick_setting_nor_flevert_switch_but_the_rest_of_the_environment()
    {
        // Final audit of v0.2.0: MAGICK_* steer the ImageMagick inside the worker (resource limits, temporary folder,
        // where it loads configuration and modules from), and FLEVERT_* are switches for tests. The names compare as
        // Windows compares them, whatever their case.
        var markers = new Dictionary<string, string?>
        {
            ["MAGICK_FLEVERT_TEST_MARKER"] = "1",
            ["flevert_test_marker"] = "1",
            ["OTHER_FLEVERT_TEST_MARKER"] = "1", // control: an unrelated variable still reaches the worker
        };
        var previous = markers.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var input = _dir.Combine("stalled.pdf");
        TestPdf.WriteStallingPage(input);
        using var cancel = new CancellationTokenSource();
        var rendering = new TaskCompletionSource();
        HashSet<string> names;
        foreach (var (name, value) in markers)
            Environment.SetEnvironmentVariable(name, value);
        var run = new PdfToImages(PdfPageImageFormat.Png, PdfToImages.Dpi, TestPdf.Worker)
            .RunAsync(input, new SyncProgress(_ => rendering.TrySetResult()), cancel.Token);
        try
        {
            await rendering.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var worker = await TestProcesses.WaitForWorkerAsync(TimeSpan.FromSeconds(5));
            names = TestProcesses.EnvironmentNames(worker);
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        Assert.Contains("OTHER_FLEVERT_TEST_MARKER", names);
        Assert.Contains("SystemRoot", names); // what .NET needs to start
        Assert.DoesNotContain("MAGICK_FLEVERT_TEST_MARKER", names);
        Assert.DoesNotContain("FLEVERT_TEST_MARKER", names);
    }

    /// <summary>Calls back at once, on the reporting thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SyncProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }
}
