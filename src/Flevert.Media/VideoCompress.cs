using System.Globalization;
using Flevert.Core;

namespace Flevert.Media;

/// <summary>A level of <c>Compress</c>: H.264 constant quality, the sound's bitrate, and how large the picture may stay.</summary>
/// <param name="Name">For the operation's id: <c>video.compress-light</c>.</param>
/// <param name="Label">As the menu shows it, saying what it does (docs/specs/media.md, "Menu").</param>
internal sealed record CompressLevel(string Name, string Label, int Crf, int AudioKbps, int? MaxShortSide = null)
{
    public static CompressLevel Light { get; } = new("light", "Light (best quality)", 23, 128);

    public static CompressLevel Balanced { get; } = new("balanced", "Balanced", 28, 96);

    /// <summary>Also at most 720p: past that, quality 32 shows its blocks more than a smaller picture would.</summary>
    public static CompressLevel Strong { get; } = new("strong", "Strong (smallest)", 32, 64, MaxShortSide: 720);

    public static IReadOnlyList<CompressLevel> All { get; } = [Light, Balanced, Strong];
}

/// <summary>
/// <c>Compress &gt; Light</c> (and Balanced, Strong): writes <c>&lt;name&gt; (compressed).mp4</c> beside the video, H.264 and
/// AAC that play everywhere, with its main sound. A result that is not smaller than the original is no use, so the file
/// is skipped and nothing is written.
/// </summary>
public sealed class VideoCompress : IOperation
{
    internal const string NotSmallerMessage = "This video is already compact: compressing it at this level would not make it smaller.";

    private readonly CompressLevel _level;
    private readonly Ffmpeg _ffmpeg;

    internal VideoCompress(CompressLevel level, Ffmpeg ffmpeg)
    {
        _level = level;
        _ffmpeg = ffmpeg;
        Id = $"video.compress-{level.Name}";
        MenuPath = $"Compress/{level.Label}";
    }

    public string Id { get; }

    public string MenuPath { get; }

    public IReadOnlyCollection<string> Extensions => MediaJob.VideoExtensions;

    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var info = await _ffmpeg.ProbeAsync(inputPath, ct).ConfigureAwait(false);
        var video = info.Video ?? throw new MediaException(MediaException.NoVideoMessage);
        var audio = VideoEncoding.FirstAudio(info);
        var size = VideoEncoding.OutputSize(video, _level.MaxShortSide);
        // At most the original: anything larger is thrown away.
        var inputBytes = new FileInfo(inputPath).Length;
        var estimate = Math.Min(VideoEncoding.EstimateBytes(info, size, _level.AudioKbps, audio.Count), inputBytes);
        var desired = MediaJob.Beside(inputPath, " (compressed).mp4");
        MediaJob.RequireSpace(desired, estimate);
        string[] options =
        [
            .. VideoEncoding.Maps(info, audio), .. MediaJob.MetadataOptions(info, audio), .. VideoEncoding.Filters(video, size),
            .. VideoEncoding.FrameTimes, .. VideoEncoding.H264(_level.Crf), .. VideoEncoding.Aac(_level.AudioKbps), "-movflags", "+faststart",
        ];
        return await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
        {
            await _ffmpeg.EncodeAsync(inputPath, options, temp, "mp4", info.Duration, MediaJob.MaxOutputBytes(inputPath, estimate, desired),
                MediaJob.Percent(progress), token, VideoEncoding.Slowness(video), VideoEncoding.DecoderThreads, boundTime: true).ConfigureAwait(false);
            // Thrown before the output is committed: WriteAtomicallyAsync deletes the temporary file.
            var outputBytes = new FileInfo(temp).Length;
            if (outputBytes >= inputBytes)
                throw new OperationSkippedException(NotSmallerMessage);
            OutputNote.AddSizes(inputBytes, outputBytes);
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>Compress &gt; Under 10 MB</c> (and 25, 100): writes <c>&lt;name&gt; (under 25 MB).mp4</c> beside the video, smaller
/// than the limit (1 MB = 1 000 000 bytes) so it goes through e-mail and chat apps. Two passes of x264 at the bitrate the
/// limit leaves (<see cref="Plan"/>), on a smaller picture when that bitrate is too low for the video's own size; an
/// output that still comes out too large is encoded again 10 % lower, twice at most.
/// </summary>
public sealed class VideoCompressUnder : IOperation
{
    private const long Megabyte = 1_000_000;
    // What the container and the bitrate's small misses take of the limit (docs/specs/media.md: × 0.97).
    private const double Margin = 0.97;
    private const int Retries = 2;
    private const double RetryFactor = 0.9;
    private const int MinVideoKbps = 100;

    private readonly int _megabytes;
    private readonly Ffmpeg _ffmpeg;

    internal VideoCompressUnder(int megabytes, Ffmpeg ffmpeg)
    {
        _megabytes = megabytes;
        _ffmpeg = ffmpeg;
        LimitBytes = megabytes * Megabyte;
        Id = string.Create(CultureInfo.InvariantCulture, $"video.compress-under-{megabytes}mb");
        MenuPath = string.Create(CultureInfo.InvariantCulture, $"Compress/Under {megabytes} MB");
    }

    public string Id { get; }

    public string MenuPath { get; }

    public IReadOnlyCollection<string> Extensions => MediaJob.VideoExtensions;

    public int MaxParallel => 1;

    /// <summary>Where each run keeps x264's statistics between its passes: a folder of its own, deleted after the run.</summary>
    internal string PassFolderRoot { get; init; } = Path.Combine(AppData.Root, AppData.MediaFolder);

    // A run's folder outlives it only when flevert.exe was killed; none runs for a day.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    /// <summary>The size the output must stay under: the menu's megabytes; smaller in tests.</summary>
    internal long LimitBytes { get; init; }

    internal string AlreadyUnderMessage => string.Create(CultureInfo.InvariantCulture, $"This video is already under {_megabytes} MB.");

    internal string TooLongMessage => string.Create(CultureInfo.InvariantCulture,
        $"Too long to fit under {_megabytes} MB: the picture would be too poor to watch. Choose a larger size.");

    internal string CouldNotFitMessage => string.Create(CultureInfo.InvariantCulture,
        $"Flevert could not make this video smaller than {_megabytes} MB. Choose a larger size.");

    /// <summary>The bitrates for the video and its sound, and the short side to scale the picture to (<c>null</c>: its own).</summary>
    internal readonly record struct Budget(int VideoKbps, int AudioKbps, int? MaxShortSide);

    // The bitrate each size needs to look decent at preset medium, by short side; below the last, the video is too long.
    private static readonly (int ShortSide, int Kbps)[] Tiers = [(2160, 8000), (1440, 5000), (1080, 2500), (720, 1200), (480, 500)];

    /// <summary>
    /// How to fit <paramref name="duration"/> into <paramref name="limitBytes"/>: bitrate = limit × 8 × 0.97 / duration,
    /// less the sound (128 kbit/s, or 96 or 64 when little is left); then the largest picture, the video's own size or
    /// 1080p, 720p, 480p, whose bitrate need is met. <c>null</c> when even 480p would get less than 100 kbit/s.
    /// </summary>
    internal static Budget? Plan(long limitBytes, TimeSpan duration, int shortSide, bool hasSound)
    {
        var totalKbps = limitBytes * 8 * Margin / duration.TotalSeconds / 1000;
        var audioKbps = !hasSound ? 0 : totalKbps >= 1000 ? 128 : totalKbps >= 400 ? 96 : 64;
        var videoKbps = totalKbps - audioKbps;
        if (videoKbps < MinVideoKbps)
            return null;
        var video = (int)Math.Min(videoKbps, int.MaxValue);
        int?[] sizes = [null, .. Tiers.Select(tier => (int?)tier.ShortSide).Where(side => side < shortSide)];
        foreach (var size in sizes)
        {
            if (video >= Need(size ?? shortSide))
                return new Budget(video, audioKbps, size);
        }

        return new Budget(video, audioKbps, shortSide > 480 ? 480 : null);
    }

    private static int Need(int shortSide) => Tiers.FirstOrDefault(tier => shortSide >= tier.ShortSide, (ShortSide: 0, Kbps: 250)).Kbps;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var info = await _ffmpeg.ProbeAsync(inputPath, ct).ConfigureAwait(false);
        var video = info.Video ?? throw new MediaException(MediaException.NoVideoMessage);
        if (new FileInfo(inputPath).Length < LimitBytes)
            throw new OperationSkippedException(AlreadyUnderMessage);
        var audio = VideoEncoding.FirstAudio(info);
        var budget = Plan(LimitBytes, info.Duration, Math.Min(video.DisplayWidth, video.DisplayHeight), audio.Count > 0)
            ?? throw new MediaException(TooLongMessage);
        var size = VideoEncoding.OutputSize(video, budget.MaxShortSide);
        var desired = MediaJob.Beside(inputPath, string.Create(CultureInfo.InvariantCulture, $" (under {_megabytes} MB).mp4"));
        MediaJob.RequireSpace(desired, LimitBytes);
        DeleteStaleRuns();
        var passes = Path.Combine(PassFolderRoot, Guid.NewGuid().ToString("N"));
        var slowness = VideoEncoding.Slowness(video);
        try
        {
            Directory.CreateDirectory(passes);
            MediaJob.RequireSpace(passes, StatisticsBytes(info, size));
            var log = Path.Combine(passes, "x264");
            // The same frames in both passes (FrameTimes), or the statistics of the first do not fit the second.
            string[] picture =
                [.. VideoEncoding.Filters(video, size), .. VideoEncoding.FrameTimes, "-c:v", "libx264", "-preset", "medium", .. VideoEncoding.EncoderThreads,
                    "-passlogfile", log];
            // The first pass only measures the picture: no sound, nothing written.
            await _ffmpeg.EncodeAsync(inputPath, ["-map", $"0:{video.Index}", .. picture, "-b:v", Kbps(budget.VideoKbps), "-pass", "1"], null, "null",
                info.Duration, 0, MediaJob.Percent(progress, 0, 0.5), ct, slowness, VideoEncoding.DecoderThreads, boundTime: true).ConfigureAwait(false);
            // One for every attempt, so a retry does not take the bar back from 100 % to 50 %.
            var secondHalf = MediaJob.Percent(progress, 0.5, 1);
            return await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
            {
                var videoKbps = (double)budget.VideoKbps;
                for (var attempt = 0; ; attempt++)
                {
                    string[] options =
                    [
                        .. VideoEncoding.Maps(info, audio), .. MediaJob.MetadataOptions(info, audio), .. picture,
                        "-b:v", Kbps((int)videoKbps), "-pass", "2", .. budget.AudioKbps > 0 ? VideoEncoding.Aac(budget.AudioKbps) : [],
                        "-movflags", "+faststart",
                    ];
                    // -fs at the limit: an output that reaches it is too large anyway (EncodeAsync reports it).
                    try
                    {
                        await _ffmpeg.EncodeAsync(inputPath, options, temp, "mp4", info.Duration, LimitBytes, secondHalf, token, slowness, VideoEncoding.DecoderThreads, boundTime: true)
                            .ConfigureAwait(false);
                        if (new FileInfo(temp).Length is var written && written < LimitBytes)
                        {
                            OutputNote.AddSizes(new FileInfo(inputPath).Length, written);
                            return;
                        }
                    }
                    catch (MediaException e) when (e.Message == MediaException.TooBigOutputMessage)
                    {
                    }

                    if (attempt == Retries)
                        throw new MediaException(CouldNotFitMessage);
                    // ffmpeg never overwrites (-n).
                    File.Delete(temp);
                    videoKbps *= RetryFactor;
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(passes, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The size of x264's statistics: its macroblock tree takes two bytes per 16 × 16 block of every frame (half a
    /// gigabyte for 20 minutes of 1080p), the rest a line of text per frame.
    /// </summary>
    internal static long StatisticsBytes(MediaInfo info, (int Width, int Height) size)
    {
        var fps = info.Video!.FrameRate is > 0 and < 240 ? info.Video.FrameRate : 30;
        var frames = info.Duration.TotalSeconds * fps;
        var blocks = Math.Ceiling(size.Width / 16.0) * Math.Ceiling(size.Height / 16.0);
        return (long)(frames * ((blocks * 2) + 200));
    }

    private void DeleteStaleRuns()
    {
        try
        {
            foreach (var run in new DirectoryInfo(PassFolderRoot).EnumerateDirectories())
            {
                if (DateTime.UtcNow - run.LastWriteTimeUtc > StaleAfter)
                    run.Delete(recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not there yet, or in use: nothing to clean, or not now.
        }
    }

    private static string Kbps(int kbps) => string.Create(CultureInfo.InvariantCulture, $"{kbps}k");
}
