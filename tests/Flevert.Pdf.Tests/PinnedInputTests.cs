using Flevert.Core;
using Flevert.Image;
using ImageMagick;

namespace Flevert.Pdf.Tests;

/// <summary>
/// On Linux whoever can write an input's folder can point its name at another file after the runner checked it (a link
/// to a private file, whose converted copy would land beside the input). Every engine reads the file the runner pinned:
/// ImageMagick in this process, qpdf, and the PDF worker. Here the name leads to a link to junk as soon as the input is
/// pinned; an operation that opened the name again would fail on the junk, or convert it.
/// </summary>
public sealed class PinnedInputTests : IDisposable
{
    private static readonly string[] Extensions = [".pdf", ".jpg", ".png"];

    private readonly TempDir _dir = new();
    private readonly TempDir _control = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _control.Dispose();
        _logs.Dispose();
    }

    public static TheoryData<string> Operations() => [.. Create().Select(operation => operation.Id)];

    [LinuxTheory]
    [MemberData(nameof(Operations))]
    public async Task Every_operation_reads_the_pinned_file_not_what_its_name_leads_to_later(string id)
    {
        var operation = Create().Single(operation => operation.Id == id);
        var extension = Extensions.First(operation.Extensions.Contains);
        // The control runs without the runner, so nothing is pinned and it reads by name.
        var control = await RunDirectlyAsync(operation, extension, _control);
        var junk = _dir.File("junk", "not what was selected");

        // The pinned file moves away (the pin keeps it), and its name becomes a link.
        var swapped = await RunAsync(operation, extension, _dir, afterPin: real =>
        {
            File.Move(real, real + ".moved");
            File.CreateSymbolicLink(real, junk);
        });

        // As if nothing had happened: the same outcome, skipped or not (a size read through the link would make a
        // compression skip), and the same output.
        Assert.True(swapped.NoFailures, string.Join("\n", swapped.Results.Select(r => r.Error)));
        Assert.Equal(control, Describe(swapped));
        Assert.Equal("not what was selected", File.ReadAllText(junk));
    }

    [LinuxFact]
    public async Task A_tools_message_names_the_file_not_the_pin_it_was_read_through()
    {
        var input = _dir.File("damaged.pdf", "%PDF-1.7 and nothing else");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(new PdfRotate(90, Qpdf.NextToApp), [input], null, CancellationToken.None);

        var error = Assert.Single(outcome.Results).Error!;
        Assert.Contains(input + ":", error, StringComparison.Ordinal);
        Assert.DoesNotContain("/proc/", error, StringComparison.Ordinal);
    }

    private async Task<BatchOutcome> RunAsync(IMenuOperation operation, string extension, TempDir dir, Action<string>? afterPin)
    {
        var inputs = Enumerable.Range(1, operation is IBatchOperation ? 2 : 1).Select(n => Input(dir, $"input {n}{extension}")).ToList();
        var runner = new Runner(new RunLog(_logs.Path)) { AfterPin = afterPin };
        return operation is IBatchOperation join
            ? await runner.RunAsync(join, inputs, null, CancellationToken.None)
            : await runner.RunAsync((IOperation)operation, inputs, null, CancellationToken.None);
    }

    private static async Task<string[]> RunDirectlyAsync(IMenuOperation operation, string extension, TempDir dir)
    {
        var inputs = Enumerable.Range(1, operation is IBatchOperation ? 2 : 1).Select(n => Input(dir, $"input {n}{extension}")).ToList();
        try
        {
            var output = operation is IBatchOperation join
                ? await join.RunAsync(inputs, null, CancellationToken.None)
                : await ((IOperation)operation).RunAsync(inputs[0], CancellationToken.None);
            return [$"done {Path.GetFileName(output)}"];
        }
        catch (OperationSkippedException)
        {
            return ["skipped "];
        }
    }

    private static string[] Describe(BatchOutcome outcome) =>
        [.. outcome.Results.Select(r => $"{(r.Skipped ? "skipped" : r.Succeeded ? "done" : "failed")} {Path.GetFileName(r.Output)}")];

    private static string Input(TempDir dir, string name)
    {
        var path = dir.Combine(name);
        // A PDF that compresses well: an operation that misreads its size would skip it.
        if (name.EndsWith(".pdf", StringComparison.Ordinal))
            TestPdf.WriteBloated(path, 2_000);
        else
            TestImages.Picture(path, 96, 64, name.EndsWith(".jpg", StringComparison.Ordinal) ? MagickFormat.Jpeg : MagickFormat.Png);
        return path;
    }

    // Every engine: the image operations, the PDF operations on qpdf, and those on the worker, built with the worker
    // this build made. Those that ask a password first are left out; they read through the same qpdf.
    private static IEnumerable<IMenuOperation> Create()
    {
        var qpdf = Qpdf.NextToApp;
        var worker = TestPdf.Worker;
        IEnumerable<IMenuOperation> operations =
        [
            .. ImageOperations.All,
            new ImagesToPdf(),
            new PdfCompress(qpdf),
            new PdfCompressSharing(qpdf, worker),
            new PdfMerge(qpdf),
            new PdfSplit(qpdf),
            new PdfRotate(90, qpdf),
            new PdfStripMetadata(qpdf),
            new PdfToImages(PdfPageImageFormat.Jpeg, 72, worker),
            .. PdfTools.All.Select(tool => PdfTools.Create(tool.OperationId,
                tool.OperationId == PdfWatermarkMenu.OperationId ? new Dictionary<string, string> { [PdfTools.Text] = "DRAFT" } : [], qpdf, worker)),
        ];
        return operations;
    }
}
