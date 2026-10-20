using System.Globalization;
using Flevert.Core;

namespace Flevert.Media;

/// <summary>Runs one tool the way <see cref="ExternalTool.RunAsync(string, IEnumerable{string}, TimeSpan, ReadOnlyMemory{byte}, string?, ExternalToolLimits?, IReadOnlyDictionary{string, string?}?, Action{string}?, CancellationToken)"/> does.</summary>
internal delegate Task<ExternalToolResult> RunTool(
    string toolPath, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? standardOutputLine, CancellationToken ct);

/// <summary>
/// ffmpeg.exe and ffprobe.exe (ADR-0009), run as separate processes through <see cref="ExternalTool"/>, below normal
/// priority. An untrusted file only ever reaches them as a local file (<c>-protocol_whitelist file</c>, input
/// <c>file:&lt;path&gt;</c>) read by one of the demuxers of <see cref="Demuxers"/>: a playlist, a concat list or a script
/// disguised as a video is refused before anything is converted, and never makes ffmpeg open a URL or another file.
/// </summary>
public sealed class Ffmpeg
{
    public const string FolderName = "ffmpeg";

    /// <summary>
    /// The demuxers an input may be read with, as ffmpeg's <c>-format_whitelist</c> matches them (<c>av_match_list</c>):
    /// <c>mov</c> is the demuxer <c>mov,mp4,m4a,3gp,3g2,mj2</c>, <c>matroska</c> is <c>matroska,webm</c>, <c>asf</c> reads
    /// WMV and WMA, <c>mpegts</c> reads MTS and M2TS. Every source type of docs/specs/media.md and nothing else.
    /// </summary>
    internal const string Demuxers = "mov,matroska,avi,asf,mpegts,mp3,wav,flac,ogg,aac,aiff";

    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private const int MaxDetailChars = 2_000;

    /// <param name="folder">Absolute path of the folder holding ffmpeg.exe and ffprobe.exe; checked when they run.</param>
    public Ffmpeg(string folder)
        : this(Path.Combine(folder, "ffmpeg.exe"), Path.Combine(folder, "ffprobe.exe"), RunExternal)
    {
    }

    internal Ffmpeg(string ffmpegPath, string ffprobePath, RunTool run)
    {
        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath;
        Run = run;
    }

    /// <summary>
    /// The <c>ffmpeg</c> folder beside the running program: in the install folder for flevert.exe (which setup checks only
    /// the user and administrators can change), or in the build output, where the Flevert.Media project copies it.
    /// </summary>
    public static Ffmpeg NextToApp { get; } = new(Path.Combine(AppContext.BaseDirectory, FolderName));

    public string FfmpegPath { get; }

    public string FfprobePath { get; }

    internal RunTool Run { get; }

    private static Task<ExternalToolResult> RunExternal(
        string toolPath, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? standardOutputLine, CancellationToken ct) =>
        ExternalTool.RunAsync(toolPath, arguments, timeout, limits: Limits, environment: ToolEnvironment(),
            standardOutputLine: standardOutputLine, cancellationToken: ct);

    /// <summary>
    /// The most memory one ffmpeg or ffprobe may commit. docs/specs/media.md asked for 2 GB, but x264 at preset medium
    /// takes about that for 4K at 60 frames a second, an iPhone's usual setting, before the HDR tone map: 3 s of it peaked
    /// at 2.2 GB in SDR and 2.5 GB in HLG on 4 cores (2026-10-20), and x264 runs more frame threads on more cores.
    /// </summary>
    internal const ulong MaxMemoryBytes = 4UL << 30;

    // ffmpeg uses every core for a video: below normal, so the machine stays responsive while it runs for minutes. Neither
    // tool starts a process of its own, so neither may.
    private static readonly ExternalToolLimits Limits = new() { BelowNormalPriority = true, SingleProcess = true, MaxMemoryBytes = MaxMemoryBytes };

    /// <summary>
    /// How the tools' environment differs from flevert.exe's: FFREPORT makes ffmpeg write a report to a file it names,
    /// and AV_LOG_FORCE_* change what it writes to stderr, which Flevert reads.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> ToolEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["FFREPORT"] = null };
        foreach (var name in Environment.GetEnvironmentVariables().Keys.OfType<string>())
        {
            if (name.StartsWith("AV_LOG_FORCE", StringComparison.OrdinalIgnoreCase))
                environment[name] = null;
        }

        return environment;
    }

    /// <summary>The options before every input: no network, no demuxer outside <see cref="Demuxers"/>.</summary>
    internal static IReadOnlyList<string> InputOptions(string inputPath) =>
        ["-protocol_whitelist", "file", "-format_whitelist", Demuxers, "-i", FileUrl(inputPath)];

    /// <summary>
    /// The <c>file:</c> URL of a fully qualified path, so ffmpeg never reads it as an option (<c>-…</c>), a protocol
    /// (<c>http:…</c>) or a pattern; ffmpeg opens the rest as it is.
    /// </summary>
    internal static string FileUrl(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"The path must be fully qualified: {path}", nameof(path));
        return "file:" + path;
    }

    /// <summary>Reads what <paramref name="inputPath"/> holds, refusing what Flevert does not convert (<see cref="MediaInfo.Parse"/>).</summary>
    /// <exception cref="MediaException">Refused, unreadable, or ffprobe took longer than <see cref="ProbeTimeout"/>.</exception>
    public async Task<MediaInfo> ProbeAsync(string inputPath, CancellationToken ct)
    {
        RequireTool(FfprobePath);
        string[] arguments =
            ["-hide_banner", "-v", "error", .. InputOptions(inputPath), "-print_format", "json", "-show_format", "-show_streams"];
        ExternalToolResult run;
        try
        {
            run = await Run(FfprobePath, arguments, ProbeTimeout, null, ct).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new MediaException(MediaException.TooSlowMessage, e);
        }

        RequireMemory(run, "ffprobe");
        if (run.ExitCode != 0 || run.StandardOutputTruncated)
            throw new MediaException(MediaException.UnreadableMessage, new FfmpegException("ffprobe", run.ExitCode, Detail(run)));
        return MediaInfo.Parse(run.StandardOutput);
    }

    /// <summary>
    /// Runs ffmpeg on one input with <paramref name="options"/> and writes <paramref name="outputPath"/> in
    /// <paramref name="format"/> (or nothing, for <c>null</c>: a first pass), reporting from 0 to 1 how much of
    /// <paramref name="duration"/> it has done.
    /// </summary>
    /// <param name="options">Output options, constants of Flevert's and numbers it computed: never anything a file or user chose.</param>
    /// <param name="maxOutputBytes">ffmpeg stops writing past this (<c>-fs</c>); an output that reached it is an error.</param>
    /// <param name="slowness">How many times the duration the pass may take, beyond its minute (<see cref="TimeoutFor"/>).</param>
    /// <exception cref="MediaException">ffmpeg failed, took longer than <see cref="TimeoutFor"/>, or the output reached <paramref name="maxOutputBytes"/>.</exception>
    /// <param name="decoding">Options for the input, before it (the decoder's thread count).</param>
    /// <param name="boundTime">Stop at <see cref="MaxTime"/>: for a video encode, where x264's statistics and the time grow
    /// with every frame. Sound and stream copies need not: <paramref name="maxOutputBytes"/> bounds them, and a raw AAC or
    /// several recordings joined have a duration ffprobe only estimates.</param>
    internal async Task EncodeAsync(
        string inputPath, IEnumerable<string> options, string? outputPath, string format, TimeSpan duration, long maxOutputBytes,
        IProgress<double>? progress, CancellationToken ct, double slowness = DefaultSlowness, IEnumerable<string>? decoding = null,
        bool boundTime = false)
    {
        RequireTool(FfmpegPath);
        var maxTime = boundTime ? MaxTime(duration, new FileInfo(inputPath).Length) : TimeSpan.Zero;
        // -n: never overwrite (the temporary output does not exist yet); -progress: key=value lines on stdout. -t before
        // the operation's options, which may shorten it (GIF).
        string[] arguments =
        [
            "-hide_banner", "-nostdin", "-nostats", "-v", "error", "-n", "-progress", "pipe:1", .. decoding ?? [],
            .. InputOptions(inputPath),
            .. boundTime ? (string[])["-t", maxTime.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)] : [], .. options,
            .. outputPath is null ? (string[])["-f", format, "-"] : ["-fs", maxOutputBytes.ToString(CultureInfo.InvariantCulture), "-f", format, FileUrl(outputPath)],
        ];
        var reader = new ProgressReader(duration, progress);
        ExternalToolResult run;
        try
        {
            run = await Run(FfmpegPath, arguments, TimeoutFor(duration, slowness), reader.OnLine, ct).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new MediaException(MediaException.TooSlowMessage, e);
        }

        RequireMemory(run, "ffmpeg");
        if (run.ExitCode != 0)
            throw new MediaException(MediaException.ToolFailedMessage, new FfmpegException("ffmpeg", run.ExitCode, Detail(run)));
        // Stopped by -t: the file holds more than it says, and a cut output must never pass for the whole (RC-1).
        if (boundTime && reader.LastTime >= maxTime - TimeSpan.FromSeconds(0.5))
        {
            throw new MediaException(MediaException.LongerThanItSaysMessage,
                new FfmpegException("ffmpeg", run.ExitCode, $"stopped at {maxTime.TotalSeconds:0.#} s; the file says {duration.TotalSeconds:0.#} s"));
        }
        if (outputPath is not null && new FileInfo(outputPath) is var written && (!written.Exists || written.Length >= maxOutputBytes))
        {
            throw new MediaException(written.Exists ? MediaException.TooBigOutputMessage : MediaException.ToolFailedMessage,
                new FfmpegException("ffmpeg", run.ExitCode, written.Exists ? "the output reached its size limit" : "no output was written"));
        }
    }

    /// <summary>
    /// How much of the input a video encode may take in (<c>-t</c>), so a file cannot hide much more than was checked
    /// (security review, SEC-1): the duration ffprobe read plus half and 10 s, or as long as the file's bytes last at
    /// 8 kbit/s, whichever is longer. ffprobe estimates the length of joined MPEG-TS recordings from the first (50 s read
    /// as 20 s), and a real file must never be cut (re-check review, RC-1). No video with its sound comes near 8 kbit/s; a
    /// file that holds more stops there, and fails (<see cref="MediaException.LongerThanItSaysMessage"/>) rather than pass
    /// a cut output for the whole. A crafted file can still claim a long time: the timeout bounds it then (RC2-5).
    /// </summary>
    internal static TimeSpan MaxTime(TimeSpan duration, long inputBytes) =>
        TimeSpan.FromSeconds(Math.Max(duration.TotalSeconds * 1.5 + 10, inputBytes * 8.0 / 8_000));

    /// <summary>
    /// The longest one pass may take: <paramref name="slowness"/> times the duration, plus a minute. Six by default
    /// (docs/specs/media.md: a slow machine encodes 1080p with preset medium at 1–2 times real time); VP9 is slower.
    /// Never more than two days: a crafted header (12 hours of 8K) would otherwise wait for weeks (RC-5).
    /// </summary>
    public static TimeSpan TimeoutFor(TimeSpan duration, double slowness = DefaultSlowness) =>
        TimeSpan.FromTicks(Math.Min((duration * slowness + TimeSpan.FromMinutes(1)).Ticks, MaxTimeout.Ticks));

    internal static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(2);

    internal const double DefaultSlowness = 6;

    // Refused memory, the tool may fail, crash or go on without it: whatever it reports cannot be trusted.
    private static void RequireMemory(ExternalToolResult run, string tool)
    {
        if (run.MemoryLimitExceeded)
            throw new MediaException(MediaException.NeedsTooMuchMemoryMessage, new FfmpegException(tool, run.ExitCode, Detail(run)));
    }

    private static void RequireTool(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{Path.GetFileName(path)} is missing from {Path.GetDirectoryName(path)}. Reinstall Flevert (in a source build, run scripts\\fetch-deps.ps1).",
                path);
        }
    }

    // The tool's last lines on stderr, where it says what went wrong.
    internal static string Detail(ExternalToolResult run)
    {
        var text = run.StandardError.Trim();
        if (text.Length == 0)
            return string.Create(CultureInfo.InvariantCulture, $"no message, exit code {run.ExitCode}");
        if (text.Length <= MaxDetailChars)
            return text;
        var start = text.Length - MaxDetailChars;
        if (char.IsLowSurrogate(text[start]))
            start++;
        return "…" + text[start..];
    }

    /// <summary>
    /// Turns ffmpeg's <c>-progress</c> lines into a fraction of the duration. ffmpeg writes a block of <c>key=value</c>
    /// lines about twice a second; <c>out_time_us</c> is how far into the output it is (<c>N/A</c> before the first frame).
    /// </summary>
    internal sealed class ProgressReader(TimeSpan duration, IProgress<double>? progress)
    {
        private double _last = -1;

        /// <summary>How far into the output ffmpeg last said it was.</summary>
        public TimeSpan LastTime { get; private set; }

        public void OnLine(string line)
        {
            double done;
            if (line == "progress=end")
            {
                done = 1;
            }
            else if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                     && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
            {
                LastTime = TimeSpan.FromMicroseconds(us);
                done = Math.Clamp(us / (duration.TotalMilliseconds * 1000), 0, 1);
            }
            else
            {
                return;
            }

            if (progress is null)
                return;
            // Never backwards: a stream ffmpeg starts late can report an earlier time for a moment.
            if (done <= _last)
                return;
            _last = done;
            progress.Report(done);
        }
    }
}
