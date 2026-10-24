using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using Flevert.Core;
using Flevert.Ocr;
using ImageMagick;

namespace Flevert.Image.Tests;

/// <summary>
/// docs/specs/ocr.md, success criterion 3: a page turned 90, 180 or 270 degrees reads right even when OSD is unsure of it,
/// as it is of pages in sans-serif fonts (Arial, Segoe UI, Tahoma score 8-9 on five lines, under the threshold of 14),
/// and a page standing upright is never turned by an unsure OSD.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ImageToTextOrientationTests : IDisposable
{
    private static readonly string[] Lines =
    [
        "Cộng hòa xã hội chủ nghĩa Việt Nam",
        "Độc lập - Tự do - Hạnh phúc",
        "Hóa đơn bán hàng kiêm phiếu xuất kho",
        "Ngày 21 tháng 10 năm 2026",
        "Khách hàng: Công ty Cổ phần Công nghệ Mới",
    ];

    private readonly TempDir _dir = new();

    // Not inside _dir: the pictures tesseract reads are kept in a plain ASCII folder of their own.
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "flevert-tests", $"ocr-scratch-{Guid.NewGuid():N}");

    public ImageToTextOrientationTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        _dir.Dispose();
        try { Directory.Delete(_scratch, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ImageToText Op(Tesseract? tesseract = null) => new(tesseract ?? Tesseract.NextToApp) { ScratchRoot = _scratch };

    // Five lines of Arial: OSD finds the right turn but scores it 8-9, so before v1.4.0's fallback the page was read as it
    // was, and at 180 and 270 degrees that gave garbage with no warning.
    [WindowsTheory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task A_sans_serif_page_turned_reads_right_when_OSD_is_unsure(int turnedClockwise)
    {
        var input = _dir.Combine("turned.png");
        WritePage(input, Lines, "Arial", turnedClockwise);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var text = await File.ReadAllTextAsync(output);
        var expected = string.Join('\n', Lines);
        var similarity = Similarity(expected, text);
        Assert.True(similarity >= 0.95, $"{similarity:P1} for the page turned {turnedClockwise}: '{text.Trim()}'");
    }

    // Two lines: OSD scores about 3 at any turn, still right about the turn; pages with no text at all score under 1.
    [WindowsTheory]
    [InlineData("Arial", 0)]
    [InlineData("Arial", 180)]
    [InlineData("Segoe UI", 270)]
    public async Task A_two_line_page_reads_right(string font, int turnedClockwise)
    {
        var input = _dir.Combine("short.png");
        string[] lines = [Lines[0], Lines[1]];
        WritePage(input, lines, font, turnedClockwise);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var text = await File.ReadAllTextAsync(output);
        var similarity = Similarity(string.Join('\n', lines), text);
        Assert.True(similarity >= 0.95, $"{similarity:P1} for {font} turned {turnedClockwise}: '{text.Trim()}'");
    }

    // OSD has never been seen to call an upright page turned, but an unsure answer must not be taken on trust: at 90
    // degrees tesseract reads the turned copy as well as the page, so the lines running across the page decide.
    [WindowsTheory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task An_upright_page_stays_as_it_is_when_an_unsure_OSD_calls_it_turned(int osdRotate)
    {
        var page = Path.Combine(_scratch, "input.png");
        WritePage(page, Lines, "Arial", 0);
        var engine = RealTesseract(osd: string.Create(CultureInfo.InvariantCulture, $"Rotate: {osdRotate}\nOrientation confidence: 8.50\n"));

        var upright = await ImageToText.ReadTextUprightAsync(engine, page, CancellationToken.None);

        Assert.Equal(0, upright.Turned);
        var similarity = Similarity(string.Join('\n', Lines), upright.Reading);
        Assert.True(similarity >= 0.95, $"{similarity:P1}: '{upright.Reading.Trim()}'");
        using var info = ImageFile.Ping(page);
        Assert.Equal((2480u, 3508u), (info[0].Width, info[0].Height));
        Assert.Equal("input.png", Path.GetFileName(Assert.Single(Directory.GetFiles(_scratch, "*.png"))));
    }

    [WindowsFact]
    public async Task A_sideways_page_an_unsure_OSD_calls_turned_is_turned_upright()
    {
        // Tesseract reads a page turned 90 degrees clockwise as well as the page turned back: the reading whose lines run
        // across the picture wins, so Make searchable can show the page upright.
        var page = Path.Combine(_scratch, "input.png");
        WritePage(page, Lines, "Arial", 90);

        var upright = await ImageToText.ReadTextUprightAsync(Tesseract.NextToApp, page, CancellationToken.None);

        Assert.Equal(270, upright.Turned);
        var similarity = Similarity(string.Join('\n', Lines), upright.Reading);
        Assert.True(similarity >= 0.95, $"{similarity:P1}: '{upright.Reading.Trim()}'");
        // The page is left as drawn (nothing reads it again), and the turned copy is gone.
        using var info = ImageFile.Ping(page);
        Assert.Equal((3508u, 2480u), (info[0].Width, info[0].Height));
        Assert.Equal("input.png", Path.GetFileName(Assert.Single(Directory.GetFiles(_scratch, "*.png"))));
    }

    public static TheoryData<string, int, int> OsdAnswers => new()
    {
        { "Rotate: 90\nOrientation confidence: 0.85\n", 1, 0 }, // under the floor: pages without text score this
        { "Rotate: 90\nOrientation confidence: 14.00\n", 1, 90 }, // sure: turned and read once, as before
        { "Rotate: 90\nOrientation confidence: 8.00\n", 2, 90 }, // unsure: both read, the turned copy reads better
        { "Rotate: 0\nOrientation confidence: 8.00\n", 1, 0 }, // upright: read once
    };

    [WindowsTheory]
    [MemberData(nameof(OsdAnswers))]
    public async Task Only_an_unsure_OSD_answer_costs_a_second_reading(string osd, int reads, int turned)
    {
        var page = Path.Combine(_scratch, "input.png");
        WriteBlank(page, 40, 20);
        var fake = new FakeTesseract(osd);

        var upright = await ImageToText.ReadTextUprightAsync(fake.Engine, page, CancellationToken.None);

        Assert.Equal(reads, fake.Reads);
        Assert.Equal(turned, upright.Turned);
        Assert.Equal(turned == 0 ? "as is" : "turned", upright.Reading.Trim());
    }

    [WindowsFact]
    public async Task Cancelling_while_both_readings_run_still_cancels()
    {
        var page = Path.Combine(_scratch, "input.png");
        WriteBlank(page, 40, 20);
        using var cts = new CancellationTokenSource();
        var fake = new FakeTesseract("Rotate: 90\nOrientation confidence: 8.00\n") { OnRead = cts.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ImageToText.ReadTextUprightAsync(fake.Engine, page, cts.Token));
    }

    // Before the fallback such a page was read once, as it is: a turned copy tesseract cannot read (a crash, too slow,
    // too large) must not fail it.
    [WindowsFact]
    public async Task The_turned_reading_failing_keeps_the_page_as_it_is()
    {
        var page = Path.Combine(_scratch, "input.png");
        WriteBlank(page, 40, 20);
        var fake = new FakeTesseract(UnsureTurned)
        {
            Read = (turned, _) => Task.FromResult(turned ? Crash : null),
        };

        var upright = await ImageToText.ReadTextUprightAsync(fake.Engine, page, CancellationToken.None);

        Assert.Equal(0, upright.Turned);
        Assert.Equal("as is", upright.Reading.Trim());
    }

    [WindowsFact]
    public async Task The_page_as_it_is_failing_keeps_the_turned_reading()
    {
        var page = Path.Combine(_scratch, "input.png");
        WriteBlank(page, 40, 20);
        var fake = new FakeTesseract(UnsureTurned)
        {
            Read = (turned, _) => Task.FromResult(turned ? null : Crash),
        };

        var upright = await ImageToText.ReadTextUprightAsync(fake.Engine, page, CancellationToken.None);

        Assert.Equal(90, upright.Turned);
        Assert.Equal("turned", upright.Reading.Trim());
    }

    [WindowsFact]
    public async Task Both_readings_failing_gives_the_error_of_the_page_as_it_is()
    {
        var page = Path.Combine(_scratch, "input.png");
        WriteBlank(page, 40, 20);
        var fake = new FakeTesseract(UnsureTurned)
        {
            Read = (turned, _) => Task.FromResult<ExternalToolResult?>(turned ? Crash : TooMuchMemory),
        };

        var error = await Assert.ThrowsAsync<OcrException>(
            () => ImageToText.ReadTextUprightAsync(fake.Engine, page, CancellationToken.None));

        Assert.Equal(OcrException.NeedsTooMuchMemoryMessage, error.Message);
    }

    // Any other failure fails the page, as a single reading would, without waiting for the other reading to end.
    [WindowsFact]
    public async Task A_reading_failing_otherwise_stops_the_other()
    {
        var page = Path.Combine(_scratch, "input.png");
        WriteBlank(page, 40, 20);
        var turnedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnedRunning = 0;
        var turnedFinished = false;
        var fake = new FakeTesseract(UnsureTurned)
        {
            Read = async (turned, ct) =>
            {
                if (!turned)
                {
                    // Fails once the turned reading runs; on a gate of one, that reading waits for this one, so not
                    // for longer than a few seconds.
                    await Task.WhenAny(turnedStarted.Task, Task.Delay(TimeSpan.FromSeconds(3), ct));
                    throw new IOException("The disk is full.");
                }

                Interlocked.Increment(ref turnedRunning);
                try
                {
                    turnedStarted.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(20), ct);
                    turnedFinished = true;
                    return null;
                }
                finally
                {
                    Interlocked.Decrement(ref turnedRunning);
                }
            },
        };

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(() => ImageToText.ReadTextUprightAsync(fake.Engine, page, CancellationToken.None));

        Assert.False(turnedFinished, $"The turned reading ran on for {clock.Elapsed}.");
        Assert.Equal(0, Volatile.Read(ref turnedRunning));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Failed after {clock.Elapsed}.");
    }

    [WindowsFact]
    public async Task Cancelling_while_both_readings_run_stops_both()
    {
        var page = Path.Combine(_scratch, "input.png");
        WriteBlank(page, 40, 20);
        using var cts = new CancellationTokenSource();
        var running = 0;
        var started = 0;
        var fake = new FakeTesseract(UnsureTurned)
        {
            Read = async (_, ct) =>
            {
                Interlocked.Increment(ref running);
                try
                {
                    // Cancelled once a reading runs; on a gate of one, the other waits for its turn and stops waiting.
                    if (Interlocked.Increment(ref started) == 1)
                        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
                    await Task.Delay(Timeout.Infinite, ct);
                    return null;
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ImageToText.ReadTextUprightAsync(fake.Engine, page, cts.Token));

        Assert.Equal(0, Volatile.Read(ref running));
    }

    private const string UnsureTurned = "Rotate: 90\nOrientation confidence: 8.00\n";

    private static readonly ExternalToolResult Crash = new(-1073741819, "", "access violation", false, false);

    private static readonly ExternalToolResult TooMuchMemory = new(1, "", "", false, false) { MemoryLimitExceeded = true };

    public static TheoryData<int, double, int, int, double, int, bool> Readings => new()
    {
        // as-is words, mean, across; turned words, mean, across; turned kept
        { 39, 60.7, 39, 39, 95.9, 39, true }, // turned 180: the turned copy reads far better
        { 39, 95.9, 39, 39, 60.7, 39, false }, // upright, OSD wrong: the page as it is reads far better
        { 39, 95.9, 0, 39, 95.9, 39, true }, // sideways: both read alike, the turned copy's lines run across
        { 39, 95.9, 39, 39, 95.9, 0, false }, // upright, OSD wrong by 90: both alike, the page's own lines run across
        { 39, 95.9, 39, 39, 96.5, 39, false }, // as good, lines alike: left as it is
        { 0, 0, 0, 0, 0, 0, false }, // nothing read either way
        { 0, 0, 0, 3, 70, 3, true }, // only the turned copy has words
        { 5, 70, 5, 0, 0, 0, false }, // only the page as it is has words
    };

    [Theory]
    [MemberData(nameof(Readings))]
    public void The_better_reading_is_kept(int words, double mean, int across, int turnedWords, double turnedMean, int turnedAcross, bool expected)
    {
        var asIs = new ReadingQuality(words, mean, across);
        var turned = new ReadingQuality(turnedWords, turnedMean, turnedAcross);

        Assert.Equal(expected, ImageToText.TurnedReadsBetter(asIs, turned));
    }

    /// <summary>A 300 DPI A4 page of <paramref name="lines"/> in 12 point <paramref name="font"/>, turned clockwise.</summary>
    private static void WritePage(string path, string[] lines, string font, int turnedClockwise)
    {
        using var bmp = new Bitmap(2480, 3508);
        bmp.SetResolution(300, 300);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var f = new Font(font, 12, FontStyle.Regular, GraphicsUnit.Point);
            g.DrawString(string.Join('\n', lines), f, Brushes.Black, new RectangleF(200, 200, 2080, 3108));
        }

        bmp.RotateFlip(turnedClockwise switch
        {
            90 => RotateFlipType.Rotate90FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        });
        bmp.Save(path, ImageFormat.Png);
    }

    private static void WriteBlank(string path, uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.White, width, height);
        image.Write(path, MagickFormat.Png);
    }

    /// <summary>The real tesseract, but OSD answers <paramref name="osd"/>.</summary>
    private static Tesseract RealTesseract(string osd) =>
        new(Tesseract.NextToApp.ExecutablePath, Tesseract.NextToApp.TessDataPath, (path, args, timeout, onLine, ct) =>
            args.Contains("osd")
                ? Task.FromResult(new ExternalToolResult(0, osd, "", false, false))
                : ExternalTool.RunAsync(path, args, timeout, environment: Tesseract.ToolEnvironment(Tesseract.NextToApp.TessDataPath), standardOutputLine: onLine, cancellationToken: ct));

    /// <summary>
    /// Answers OSD as set; reads "as is" from a picture wider than tall (as <see cref="WriteBlank"/> writes it) and
    /// "turned" from one turned a quarter, each with the words tesseract's TSV would give: the turned one reads better.
    /// </summary>
    private sealed class FakeTesseract(string osd)
    {
        private int _reads;

        public int Reads => _reads;

        public Action? OnRead { get; init; }

        /// <summary>
        /// Runs first for each reading, told whether it reads the turned copy: the run tesseract's would end in, or null
        /// to read as above. It may also throw, or wait for the reading to be cancelled.
        /// </summary>
        public Func<bool, CancellationToken, Task<ExternalToolResult?>>? Read { get; init; }

        public Tesseract Engine => new(Tesseract.NextToApp.ExecutablePath, Tesseract.NextToApp.TessDataPath, Run);

        private async Task<ExternalToolResult> Run(string path, IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            if (args.Contains("osd"))
                return new ExternalToolResult(0, osd, "", false, false);

            Interlocked.Increment(ref _reads);
            OnRead?.Invoke();
            ct.ThrowIfCancellationRequested();
            bool turned;
            using (var info = ImageFile.Ping(args[0]))
                turned = info[0].Width < info[0].Height;
            if (Read is not null && await Read(turned, ct) is { } run)
                return run;

            var text = turned ? "turned" : "as is";
            if (args[1] == "stdout")
                return new ExternalToolResult(0, text, "", false, false);

            var confidence = turned ? "95.5" : "55.5";
            File.WriteAllText(args[1] + ".txt", text);
            File.WriteAllText(args[1] + ".tsv", $"""
                level	page_num	block_num	par_num	line_num	word_num	left	top	width	height	conf	text
                1	1	0	0	0	0	0	0	40	20	-1
                4	1	1	1	1	0	2	2	30	8	-1
                5	1	1	1	1	1	2	2	30	8	{confidence}	{text}

                """);
            return new ExternalToolResult(0, "", "", false, false);
        }
    }

    private static double Similarity(string expected, string actual)
    {
        static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var e = Normalize(expected);
        var a = Normalize(actual);
        if (e == a)
            return 1;
        if (e.Length == 0 || a.Length == 0)
            return 0;
        var previous = new int[a.Length + 1];
        var current = new int[a.Length + 1];
        for (var j = 0; j <= a.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= e.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= a.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (e[i - 1] == a[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return 1 - ((double)previous[a.Length] / Math.Max(e.Length, a.Length));
    }
}
