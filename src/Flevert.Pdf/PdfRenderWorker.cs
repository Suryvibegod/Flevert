using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using Microsoft.Win32.SafeHandles;
using Flevert.Image;

namespace Flevert.Pdf;

/// <summary>What a worker is asked to do with the PDF (<see cref="PdfRenderRequest.Task"/>).</summary>
internal enum PdfWorkerTask
{
    /// <summary>Render every page into the folder, in <see cref="PdfRenderRequest.Format"/> at <see cref="PdfRenderRequest.Dpi"/>.</summary>
    Pictures,

    /// <summary>Write the text into the new file the request's folder field names (<see cref="PdfExtractText"/>).</summary>
    Text,

    /// <summary>Write a smaller JPEG of each image drawn above <see cref="PdfRenderRequest.Dpi"/> into the folder (<see cref="PdfCompressSharing"/>).</summary>
    ShrinkImages,

    /// <summary>Say which pages are blank, drawn at <see cref="PdfRenderRequest.Dpi"/> (<see cref="PdfRemoveBlankPages"/>); nothing is written.</summary>
    BlankPages,

    /// <summary>Make the edit <see cref="PdfRenderRequest.Options"/> describes (<see cref="PdfEdit"/>) and save the PDF as the new file the folder field names.</summary>
    Edit,

    /// <summary>Write every image the pages draw into the folder (<see cref="PdfExtractImages"/>).</summary>
    ExtractImages,
}

/// <summary>
/// What one worker is asked to do (<see cref="Task"/>) with <see cref="InputPath"/>, read through <see cref="ReadPath"/>,
/// which must be the file <see cref="Input"/>, and where the result goes (<see cref="Folder"/>). It travels on the worker's
/// standard input, never its command line.
/// </summary>
/// <remarks>
/// The fields are the raw UTF-16 code units of their text, each ended by U+0000, which no path can hold: a Windows name may
/// hold a lone surrogate, which UTF-8 (and .NET's encoders) would replace with U+FFFD, naming another file (review round 1,
/// SA-M1, for qpdf). The tasks of v0.2.2 (<see cref="PdfWorkerTask.BlankPages"/> and after) have an eighth field,
/// <see cref="Options"/>; the requests of the tasks before keep their seven.
/// </remarks>
internal sealed record PdfRenderRequest(string InputPath, FileIdentity Input, string Folder, PdfPageImageFormat Format, double Dpi)
{
    public PdfWorkerTask Task { get; init; }

    /// <summary>
    /// Where the worker opens the input: <see cref="InputPath"/>, which names the outputs, or on Linux the runner's pin of
    /// it (<see cref="Flevert.Core.PinnedInput.ReadPath"/>), since the name may lead elsewhere by now.
    /// </summary>
    public string ReadPath { get; init; } = InputPath;

    /// <summary>For the tasks of v0.2.2: what to do, such as the edit (<see cref="PdfEdit.Encode"/>); empty when nothing more is needed.</summary>
    public string Options { get; init; } = "";

    private bool HasOptions => Task is PdfWorkerTask.BlankPages or PdfWorkerTask.Edit or PdfWorkerTask.ExtractImages;

    /// <summary>
    /// Changes whenever a field of a task already sent changes its meaning, so a worker of another version refuses the
    /// request rather than misreading it. A new task may add a field without changing it: a worker that does not know the
    /// task refuses the request by its task name, and the count of fields is checked per task.
    /// </summary>
    internal const string Magic = "flevert-pdf-worker/3";

    /// <summary>Three paths of at most 32 767 characters and a few short fields fit with room to spare.</summary>
    internal const int MaxBytes = 256 * 1024;

    private const double MaxDpi = 10_000;

    public byte[] Encode()
    {
        string[] fields =
        [
            Magic, InputPath, ReadPath, Input.ToString(), Folder,
            Task switch
            {
                PdfWorkerTask.Text => "text",
                PdfWorkerTask.ShrinkImages => "shrink",
                PdfWorkerTask.BlankPages => "blank",
                PdfWorkerTask.Edit => "edit",
                PdfWorkerTask.ExtractImages => "images",
                _ => Format == PdfPageImageFormat.Jpeg ? "jpeg" : "png",
            },
            Dpi.ToString("R", CultureInfo.InvariantCulture),
            .. HasOptions ? [Options] : Array.Empty<string>(),
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
        var withOptions = fields.Length > 5 && fields[5] is "blank" or "edit" or "images";
        if (fields.Length != (withOptions ? 8 : 7) || fields[0] != Magic)
            throw new FormatException("Not a request of this version.");

        var (input, readPath, identity, folder, format, dpiText) = (fields[1], fields[2], fields[3], fields[4], fields[5], fields[6]);
        if (!Path.IsPathFullyQualified(input) || !Path.IsPathFullyQualified(readPath) || !Path.IsPathFullyQualified(folder))
            throw new FormatException("The request's paths must be fully qualified.");
        var imageFormat = format switch
        {
            "jpeg" or "text" or "shrink" or "blank" or "edit" or "images" => PdfPageImageFormat.Jpeg,
            "png" => PdfPageImageFormat.Png,
            _ => throw new FormatException($"Unknown image format: {format}"),
        };
        if (!double.TryParse(dpiText, NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) || !double.IsFinite(dpi) || dpi <= 0 || dpi > MaxDpi)
            throw new FormatException($"Not a DPI: {dpiText}");
        var task = format switch
        {
            "text" => PdfWorkerTask.Text,
            "shrink" => PdfWorkerTask.ShrinkImages,
            "blank" => PdfWorkerTask.BlankPages,
            "edit" => PdfWorkerTask.Edit,
            "images" => PdfWorkerTask.ExtractImages,
            _ => PdfWorkerTask.Pictures,
        };
        // An edit this version does not know is refused with the rest of the request; the tasks that take no options
        // must have none, so a field this version would ignore is never read as something else.
        if (task == PdfWorkerTask.Edit)
            PdfEdit.Decode(fields[7]);
        else if (withOptions && fields[7].Length != 0)
            throw new FormatException($"The {format} task takes no options.");
        return new PdfRenderRequest(input, FileIdentity.Parse(identity), folder, imageFormat, dpi)
        {
            Task = task,
            ReadPath = readPath,
            Options = withOptions ? fields[7] : "",
        };
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
/// could not be set up; <c>NoText</c> when a PDF has no text to extract), exit code 1.</item>
/// </list>
/// A text request says the same, with <c>page &lt;n&gt; 1 1</c> before page n is read and <c>wrote &lt;n&gt;</c> once
/// its text is in the file; there is no <c>busy</c>. A request to shrink images says <c>page</c> and <c>wrote</c> as it
/// looks at each page's images, then, once every page is seen, <c>encode &lt;hash&gt; &lt;width&gt; &lt;height&gt;</c>
/// before it decodes and re-encodes an image of that pixel size, followed by <c>image &lt;hash&gt; &lt;width&gt;
/// &lt;height&gt; &lt;gray|rgb&gt;</c> once <c>&lt;hash&gt;.jpg</c> is written, or <c>kept &lt;hash&gt;</c> when it is
/// left as it is; then <c>done</c>.
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
            file = File.OpenHandle(request.ReadPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            // The parent holds the file open (with read-only sharing on Windows; on Linux the path is its pin), so the path
            // still leads to it; checked anyway, so a path that leads anywhere else is never read.
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
            switch (request.Task)
            {
                case PdfWorkerTask.Text:
                    ExtractText(request, file!, lines);
                    break;
                case PdfWorkerTask.ShrinkImages:
                    ShrinkImages(request, file!, lines);
                    break;
                case PdfWorkerTask.BlankPages:
                    PdfToolsWorker.FindBlankPages(request, file!, lines);
                    break;
                case PdfWorkerTask.Edit:
                    PdfToolsWorker.Edit(request, file!, lines);
                    break;
                case PdfWorkerTask.ExtractImages:
                    PdfToolsWorker.ExtractImages(request, file!, lines);
                    break;
                default:
                    Render(request, file!, lines);
                    break;
            }
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
        catch (NoTextException e)
        {
            lines.WriteLine($"error {NoTextCode} {OneLine(e.Message)}");
            return ExitFailed;
        }
        catch (SkipException e)
        {
            lines.WriteLine($"error {SkippedCode} {OneLine(e.Message)}");
            return ExitFailed;
        }
    }

    /// <summary>The error code of a PDF the tool has nothing to do for: the parent skips the file with the message.</summary>
    internal const string SkippedCode = "Skipped";

    /// <summary>The error code of a PDF without text: the parent skips the file rather than failing it.</summary>
    internal const string NoTextCode = "NoText";

    internal const string NoTextMessage =
        "This PDF has no text to extract: it may be a scanned document (Flevert does not recognise text in pictures).";

    /// <summary>The most text written for one PDF (docs/specs/pdf.md, P3).</summary>
    internal const long MaxTextBytes = 100L * 1024 * 1024;

    internal static readonly string TooMuchTextMessage = string.Create(CultureInfo.InvariantCulture,
        $"The text of this PDF is larger than {MaxTextBytes >> 20} MB, the most Flevert writes.");

    private static void ExtractText(PdfRenderRequest request, SafeFileHandle file, StreamWriter lines)
    {
        using var pdf = PdfDocument.Open(file, PdfLimits.Default);
        var total = pdf.PageCount;
        if (total == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");
        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"pages {total}"));

        bool anyText;
        try
        {
            // CreateNew: the parent hands a fresh name beside the input, where others may be able to add entries.
            using var stream = new FileStream(request.Folder, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            anyText = WriteText(pdf.GetText, total, stream, MaxTextBytes,
                page => lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {page} 1 1")),
                page => lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {page}")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PageWriteException($"Could not write the text: {e.Message}", e);
        }

        if (!anyText)
            throw new NoTextException(NoTextMessage);
        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done {total}"));
    }

    /// <summary>
    /// Writes the text of <paramref name="total"/> pages to <paramref name="output"/>: UTF-8 with a byte order mark, so
    /// Notepad of older Windows reads the accents right; lines ended by <c>\r\n</c>; a form feed before every page but
    /// the first. Returns whether any page had text. Nothing goes past <paramref name="maxBytes"/>: the PDF fails as too
    /// large, and the caller deletes what was written.
    /// </summary>
    /// <param name="pageText">The text of a page, by index, as pdfium gives it.</param>
    internal static bool WriteText(Func<int, string> pageText, int total, Stream output, long maxBytes, Action<int> started, Action<int> wrote)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        long bytes = utf8.GetPreamble().Length;
        var anyText = false;
        using var writer = new StreamWriter(output, utf8, leaveOpen: true);
        for (var index = 0; index < total; index++)
        {
            var page = index + 1;
            started(page);
            var text = PageText(pageText(index));
            anyText |= text.Length > 0;
            var block = (index == 0 ? "" : "\f") + text + "\r\n";
            bytes += utf8.GetByteCount(block);
            if (bytes > maxBytes)
                throw new PdfException(PdfError.TooLarge, TooMuchTextMessage);
            writer.Write(block);
            writer.Flush();
            wrote(page);
        }

        return anyText;
    }

    /// <summary>The JPEG quality of a shrunk image (docs/specs/pdf.md, P1: 150 DPI, quality 80).</summary>
    internal const uint SharingJpegQuality = 80;

    // Filters of 1-bit images, which JPEG would blur and make larger.
    private static readonly HashSet<string> KeptFilters = new(StringComparer.Ordinal) { "JBIG2Decode", "CCITTFaxDecode" };

    // Every image of every page, then a JPEG at request.Dpi of each one drawn above it everywhere it is drawn. The images
    // are told apart by the SHA-256 of their stored data, which the parent finds again in qpdf's description of the PDF.
    private static void ShrinkImages(PdfRenderRequest request, SafeFileHandle file, StreamWriter lines)
    {
        using var pdf = PdfDocument.Open(file, PdfLimits.Default);
        var total = pdf.PageCount;
        if (total == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");
        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"pages {total}"));

        var found = new Dictionary<string, ImageSeen>(StringComparer.Ordinal);
        for (var index = 0; index < total; index++)
        {
            var page = index + 1;
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {page} 1 1"));
            foreach (var image in pdf.GetImages(index))
            {
                // The largest size it is drawn at decides: the lowest resolution. Inside a form, the size is not known.
                var keep = image.InForm || image.BitsPerPixel <= 1 || image.Filters.Any(KeptFilters.Contains)
                    || (long)image.Width * image.Height > (long)PdfDocument.MaxRenderPixels * PdfDocument.MaxRenderPixels;
                found[image.Hash] = found.TryGetValue(image.Hash, out var seen)
                    ? seen with { Dpi = Math.Min(seen.Dpi, image.Dpi), Keep = seen.Keep || keep }
                    : new ImageSeen(index, image.ObjectIndex, image.Width, image.Height, image.Dpi, keep);
            }

            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {page}"));
        }

        var shrink = found.Where(entry => !entry.Value.Keep && entry.Value.Dpi > request.Dpi).ToList();
        if (shrink.Count > 0)
        {
            try
            {
                MagickSetup.EnsureInitialized();
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or MagickException)
            {
                throw new SetupException($"Flevert could not set up its image encoder: {e.Message}", e);
            }
        }

        foreach (var (hash, seen) in shrink)
        {
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"encode {hash} {seen.Width} {seen.Height}"));
            if (pdf.GetImagePixels(seen.PageIndex, seen.ObjectIndex) is not { } pixels)
            {
                lines.WriteLine($"kept {hash}");
                continue;
            }

            var scale = request.Dpi / seen.Dpi;
            var width = Math.Max(1, (int)Math.Round(pixels.Width * scale));
            var height = Math.Max(1, (int)Math.Round(pixels.Height * scale));
            // The dictionary says what the JPEG holds, from its frame header: ImageMagick writes one component for an RGB
            // image whose every pixel is gray (probed), which is smaller and draws the same.
            var gray = EncodeShrunk(pixels, width, height, Path.Combine(request.Folder, hash + ".jpg")) == 1;
            lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"image {hash} {width} {height} {(gray ? "gray" : "rgb")}"));
        }

        lines.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done {total}"));
    }

    private sealed record ImageSeen(int PageIndex, int ObjectIndex, int Width, int Height, double Dpi, bool Keep);

    // Returns the number of components of the JPEG written: 1 or 3.
    private static int EncodeShrunk(PdfImagePixels pixels, int width, int height, string path)
    {
        using var image = new MagickImage();
        // Raw pixels in memory, as for pages: no coder parses anything.
        var settings = pixels.Gray
            ? new PixelReadSettings((uint)pixels.Width, (uint)pixels.Height, StorageType.Char, "I")
            : new PixelReadSettings((uint)pixels.Width, (uint)pixels.Height, StorageType.Char, PixelMapping.BGR);
        image.ReadPixels(pixels.Pixels, settings);
        if (pixels.Gray)
            image.ColorType = ColorType.Grayscale;
        image.Resize((uint)width, (uint)height);
        image.Quality = SharingJpegQuality;
        image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
        var jpeg = image.ToByteArray(MagickFormat.Jpeg);
        var components = JpegComponents(jpeg);
        if (components is not (1 or 3))
            throw new InvalidOperationException($"ImageMagick wrote a JPEG of {components} components.");
        try
        {
            // CreateNew in the parent's private folder: nothing there is ever replaced.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(jpeg);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PageWriteException($"Could not write a smaller image: {e.Message}", e);
        }

        return components;
    }

    /// <summary>The number of components in the frame header (SOF0 to SOF2) of a JPEG ImageMagick wrote; 0 when there is none.</summary>
    internal static int JpegComponents(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return 0;
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            var marker = jpeg[at + 1];
            var length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (length < 2)
                return 0;
            if (marker is 0xC0 or 0xC1 or 0xC2)
                return at + 9 < jpeg.Length && length >= 8 ? jpeg[at + 9] : 0;
            at += 2 + length;
        }

        return 0;
    }

    /// <summary>
    /// A page's text as written: every line ended by <c>\r\n</c>, without NUL, form feeds of its own or the noncharacters
    /// pdfium can give for a glyph without a meaning, every blank space as a plain one, and without the blank lines and
    /// spaces it ends with; empty for a page with nothing but white space.
    /// </summary>
    /// <remarks>
    /// A font that draws the space and the no-break space with one glyph (Arial does) maps that glyph back to only one of
    /// them, so a PDF can give U+00A0 between every word; searched or pasted, it does not match a space. Only the spaces
    /// that are blank (<see cref="IsBlankSpace"/>) become one: U+1680, also a space separator, is a visible stroke.
    /// </remarks>
    internal static string PageText(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (IsBlankSpace(c))
                clean.Append(' ');
            else if (c is not ('\0' or '\f' or '\uFFFE' or '\uFFFF'))
                clean.Append(c);
        }

        return clean.ToString().ReplaceLineEndings("\r\n").TrimEnd();
    }

    // The blank spaces of Unicode: no-break, the typographic widths (en quad to hair), narrow no-break, medium
    // mathematical and ideographic.
    private static bool IsBlankSpace(char c) => c is '\u00A0' or (>= '\u2000' and <= '\u200A') or '\u202F' or '\u205F' or '\u3000';

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
    internal sealed class PageWriteException(string message, Exception? inner) : Exception(message, inner);

    /// <summary>The PDF has no text at all.</summary>
    private sealed class NoTextException(string message) : Exception(message);

    /// <summary>
    /// What the worker needs from the machine is missing or could not be set up (ImageMagick's configuration folder could
    /// not be written, Windows' Arial is missing): not the PDF's fault.
    /// </summary>
    internal sealed class SetupException(string message, Exception? inner) : Exception(message, inner);

    /// <summary>The tool has nothing to do for this PDF (no blank page, no image): the file is skipped with the message.</summary>
    internal sealed class SkipException(string message) : Exception(message);
}
