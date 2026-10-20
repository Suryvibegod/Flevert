using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using Microsoft.Win32.SafeHandles;
using Flevert.Image;

namespace Flevert.Pdf;

/// <summary>
/// What one worker is asked to do: render <see cref="InputPath"/>, which must be the file <see cref="Input"/>, into
/// <see cref="Folder"/>. It travels on the worker's standard input, never its command line.
/// </summary>
/// <remarks>
/// The fields are the raw UTF-16 code units of their text, each ended by U+0000, which no path can hold: a Windows name may
/// hold a lone surrogate, which UTF-8 (and .NET's encoders) would replace with U+FFFD, naming another file (review round 1,
/// SA-M1, for qpdf).
/// </remarks>
internal sealed record PdfRenderRequest(string InputPath, FileIdentity Input, string Folder, PdfPageImageFormat Format, double Dpi)
{
    /// <summary>Changes whenever the fields change, so a worker of another version refuses the request rather than misreading it.</summary>
    internal const string Magic = "flevert-pdf-worker/1";

    /// <summary>Two paths of at most 32 767 characters and a few short fields fit with room to spare.</summary>
    internal const int MaxBytes = 256 * 1024;

    private const double MaxDpi = 10_000;

    public byte[] Encode()
    {
        string[] fields =
        [
            Magic, InputPath, Input.ToString(), Folder, Format == PdfPageImageFormat.Jpeg ? "jpeg" : "png",
            Dpi.ToString("R", CultureInfo.InvariantCulture),
        ];
        if (fields.Any(field => field.Contains('\0', StringComparison.Ordinal)))
            throw new ArgumentException("A field of the request holds a null character.");
        var text = string.Concat(fields.Select(field => field + "\0"));
        return MemoryMarshal.AsBytes(text.AsSpan()).ToArray();
    }

    /// <exception cref="FormatException">Not a request this version writes, or one with a value it would not send.</exception>
    public static PdfRenderRequest Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 2 != 0 || bytes.Length > MaxBytes)
            throw new FormatException("The request has the wrong length.");
        var text = new string(MemoryMarshal.Cast<byte, char>(bytes));
        if (!text.EndsWith('\0'))
            throw new FormatException("The request is incomplete.");
        var fields = text[..^1].Split('\0');
        if (fields.Length != 6 || fields[0] != Magic)
            throw new FormatException("Not a request of this version.");

        var (input, identity, folder, format, dpiText) = (fields[1], fields[2], fields[3], fields[4], fields[5]);
        if (!Path.IsPathFullyQualified(input) || !Path.IsPathFullyQualified(folder))
            throw new FormatException("The request's paths must be fully qualified.");
        var imageFormat = format switch
        {
            "jpeg" => PdfPageImageFormat.Jpeg,
            "png" => PdfPageImageFormat.Png,
            _ => throw new FormatException($"Unknown image format: {format}"),
        };
        if (!double.TryParse(dpiText, NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) || !double.IsFinite(dpi) || dpi <= 0 || dpi > MaxDpi)
            throw new FormatException($"Not a DPI: {dpiText}");
        return new PdfRenderRequest(input, FileIdentity.Parse(identity), folder, imageFormat, dpi);
    }
}

/// <summary>
/// The worker side of <see cref="PdfWorker"/>: what <c>flevert.exe pdf-worker</c> runs. Reads one request from standard
/// input, renders and encodes every page into the folder it names, and says what it does, one line at a time, on standard
/// output (ADR-0008, "Reading untrusted PDFs in a worker"):
/// <list type="bullet">
/// <item><c>pages &lt;count&gt;</c> once the PDF is open, after which the size of every page is read
/// (<see cref="PdfRenderBudget"/>);</item>
/// <item><c>page &lt;n&gt; &lt;width&gt; &lt;height&gt;</c> before pdfium reads and draws page n, at that pixel size;</item>
/// <item><c>busy &lt;n&gt;</c> at most twice a second while ImageMagick encodes page n;</item>
/// <item><c>wrote &lt;n&gt;</c> once page n is written; <c>done &lt;count&gt;</c> at the end, exit code 0;</item>
/// <item><c>error &lt;code&gt; &lt;message&gt;</c> as the last line when it cannot go on (a <see cref="PdfError"/> name;
/// <c>Write</c> when a page could not be written, or the disk has not the room for them all; <c>Setup</c> when ImageMagick
/// could not be set up), exit code 1.</item>
/// </list>
/// A request it cannot read, or an input that is not the file the request names, ends it with exit code 2 and nothing on
/// standard output. Anything else (a crash in pdfium, a kill) is whatever the process ends with.
/// </summary>
public static class PdfRenderWorker
{
    internal const int ExitDone = 0;
    internal const int ExitFailed = 1;
    internal const int ExitRefused = 2;

    private const uint JpegQuality = 90;
    private const int MaxMessageChars = 500;
    private static readonly TimeSpan BusyEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>Reads the request from <paramref name="input"/>, renders, reports on <paramref name="output"/>; returns the exit code.</summary>
    public static int Run(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var lines = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
        PdfRenderRequest request;
        SafeFileHandle? file = null;
        try
        {
            request = PdfRenderRequest.Decode(ReadRequest(input));
            file = File.OpenHandle(request.InputPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            // The parent holds the file open with read-only sharing, so the path still names it; checked anyway, so a path
            // that leads anywhere else is never read.
            if (FileIdentity.Of(file) != request.Input)
                throw new IOException("the input is not the file Flevert holds.");
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            file?.Dispose();
            Console.Error.WriteLine($"pdf-worker: refused: {e.Message}");
            return ExitRefused;
        }

        try
        {
            Render(request, file!, lines);
            return ExitDone;
        }
        catch (PdfException e)
        {
            lines.WriteLine($"error {e.Error} {OneLine(e.Message)}");
            return ExitFailed;
        }
        catch (Exception e) when (e is OutOfMemoryException or MagickResourceLimitErrorException)
        {
            // The parent words it with the limit it set; the job's own report may come too.
            lines.WriteLine($"error {PdfError.NeedsTooMuchMemory} The page needs more memory than the worker may use.");
            return ExitFailed;
        }
        catch (PageWriteException e)
        {
            lines.WriteLine($"error Write {OneLine(e.Message)}");
            return ExitFailed;
        }
        catch (SetupException e)
        {
            lines.WriteLine($"error Setup {OneLine(e.Message)}");
            return ExitFailed;
        }
    }

    private static void Render(PdfRenderRequest request, SafeFileHandle file, StreamWriter lines)
    {
        using var pdf = PdfDocument.Open(file, PdfLimits.Default);
        var total = pdf.PageCount;
        if (total == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");
        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"pages {total}"));

        // Every page's pixel size, before any is drawn: too many pixels in all, or not the room for them, ends it here.
        long allPixels = 0;
        for (var index = 0; index < total; index++)
        {
            var size = pdf.GetRenderSize(index, request.Dpi);
            allPixels += (long)size.Width * size.Height;
        }

        if (PdfRenderBudget.Check(allPixels, request.Format, PdfRenderBudget.FreeBytes(request.Folder)) is { } noRoom)
            throw new PageWriteException(noRoom, null);

        try
        {
            MagickSetup.EnsureInitialized();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or MagickException)
        {
            // Not the PDF's fault: say what failed rather than let it read as a damaged PDF.
            throw new SetupException($"Flevert could not set up its image encoder: {e.Message}", e);
        }
        var stem = Path.GetFileNameWithoutExtension(request.InputPath);
        // One buffer for every page, grown for the largest so far (review round 1, CR-11, CR-12).
        byte[] pixels = [];
        for (var index = 0; index < total; index++)
        {
            var page = index + 1;
            var size = pdf.GetRenderSize(index, request.Dpi);
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {page} {size.Width} {size.Height}"));
            if (pixels.Length < size.Bytes)
            {
                pixels = []; // let the smaller one go before asking for the larger
                pixels = GC.AllocateUninitializedArray<byte>(checked((int)size.Bytes));
            }

            pdf.RenderInto(index, request.Dpi, pixels);
            var name = PdfToImages.PageName(stem, page, total, request.Format);
            Encode(pixels.AsSpan(0, (int)size.Bytes), size, request.Format, Path.Combine(request.Folder, name), Busy(lines, page));
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {page}"));
        }

        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done {total}"));
    }

    // "busy <n>" at most every BusyEvery while ImageMagick works on page n: proof of work, so the parent's watchdog
    // allows a large page the time it takes to encode.
    private static Action Busy(StreamWriter lines, int page)
    {
        var since = Stopwatch.StartNew();
        return () =>
        {
            if (since.Elapsed < BusyEvery)
                return;
            since.Restart();
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"busy {page}"));
        };
    }

    private static void Encode(ReadOnlySpan<byte> bgra, PdfRenderSize size, PdfPageImageFormat format, string path, Action busy)
    {
        using var image = new MagickImage();
        // Raw pixels in memory: no coder, so no file format is parsed here.
        image.ReadPixels(bgra, new PixelReadSettings((uint)size.Width, (uint)size.Height, StorageType.Char, PixelMapping.BGRA));
        // pdfium drew the page on white, so every pixel is opaque: RGB output, no alpha channel.
        image.Alpha(AlphaAction.Off);
        // Prints at the page's real size.
        image.Density = new Density(size.Dpi, size.Dpi, DensityUnit.PixelsPerInch);
        if (format == PdfPageImageFormat.Jpeg)
        {
            image.Quality = JpegQuality;
            // ImageMagick keeps full colour resolution from quality 90 up; pages are for reading and sharing, and 4:2:0
            // is the smaller file (spec: "nhỏ").
            image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
        }

        image.Progress += (_, _) => busy();
        try
        {
            // Written as it is encoded, not held whole in memory first. CreateNew: others who can write beside the input
            // can add entries to the folder, and nothing they put there is ever replaced (ADR-0007).
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            image.Write(stream, format == PdfPageImageFormat.Jpeg ? MagickFormat.Jpeg : MagickFormat.Png);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PageWriteException($"Could not write {Path.GetFileName(path)}: {e.Message}", e);
        }
    }

    private static byte[] ReadRequest(Stream input)
    {
        var request = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (request.Length + read > PdfRenderRequest.MaxBytes)
                throw new FormatException("The request is too long.");
            request.Write(buffer, 0, read);
        }

        return request.ToArray();
    }

    /// <summary>A message as one line of the protocol: no line breaks or other control characters, and not too long.</summary>
    internal static string OneLine(string message)
    {
        var text = new string([.. message.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        return text.Length <= MaxMessageChars ? text : text[..MaxMessageChars];
    }

    /// <summary>A page could not be written to the folder (disk full, access denied): not the PDF's fault.</summary>
    private sealed class PageWriteException(string message, Exception? inner) : Exception(message, inner);

    /// <summary>ImageMagick could not be set up in the worker (its configuration folder could not be written).</summary>
    private sealed class SetupException(string message, Exception inner) : Exception(message, inner);
}
