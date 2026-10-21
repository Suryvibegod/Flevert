using Flevert.Core;
using static Flevert.Media.Tests.VideoConvertTests;

namespace Flevert.Media.Tests;

/// <summary>Compress &gt; Light, Balanced, Strong, and Under 10 / 25 / 100 MB (docs/specs/media.md; success criterion 2).</summary>
public sealed class VideoCompressTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string id) => MediaOperations.Create(TestFfmpeg.Instance).OfType<IOperation>().Single(op => op.Id == id);

    [Fact]
    public void Compress_has_three_levels_and_three_sizes_for_every_video_type()
    {
        var compress = MediaOperations.Create(TestFfmpeg.Instance).OfType<IOperation>().Where(op => op.MenuPath.StartsWith("Compress/", StringComparison.Ordinal)).ToList();

        Assert.Equal(
            ["Compress/Light (best quality)", "Compress/Balanced", "Compress/Strong (smallest)", "Compress/Under 10 MB", "Compress/Under 25 MB",
                "Compress/Under 100 MB"],
            compress.Select(op => op.MenuPath));
        Assert.Equal(
            ["video.compress-light", "video.compress-balanced", "video.compress-strong", "video.compress-under-10mb", "video.compress-under-25mb",
                "video.compress-under-100mb"],
            compress.Select(op => op.Id));
        Assert.All(compress, op => Assert.Equal(MediaJob.VideoExtensions, op.Extensions));
        Assert.All(compress, op => Assert.Equal(1, op.MaxParallel));
    }

    [Fact]
    public async Task Light_writes_a_smaller_mp4_at_the_same_size_beside_the_video()
    {
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mp4");

        var output = await Op("video.compress-light").RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("clip (compressed).mp4"), output);
        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length, $"{new FileInfo(input).Length} → {new FileInfo(output).Length}");
        var probe = await Probe(output);
        Assert.Equal(["h264", "aac"], probe.Streams.Select(s => s.Codec));
        Assert.Equal((640, 360), (probe.Streams[0].Width, probe.Streams[0].Height));
        Assert.Equal(4, probe.Duration, 1);
    }

    [Fact]
    public async Task Strong_also_brings_a_1080p_video_down_to_720p()
    {
        var input = await Fixtures.CopyAsync(Fixtures.Video1080pAsync(), _dir, "clip.mp4");

        var strong = await Op("video.compress-strong").RunAsync(input, CancellationToken.None);

        var probe = await Probe(strong);
        Assert.Equal((1280, 720), (probe.Streams[0].Width, probe.Streams[0].Height));
        Assert.Equal(10, probe.Duration, 1);
    }

    [WindowsFact]
    public async Task The_notification_names_the_new_size_and_the_old_one()
    {
        // docs/specs/media.md: "Done: clip (under 25 MB).mp4 (24.3 MB, was 412 MB)".
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mp4");
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op("video.compress-light"), [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(new SizeChange(new FileInfo(input).Length, new FileInfo(result.Output!).Length), result.Sizes);
        Assert.StartsWith("Done: clip (compressed).mp4 (", BatchSummary.Describe(outcome, logs.Path).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_video_compress_would_not_make_smaller_is_skipped_and_nothing_is_written()
    {
        // Already at a far lower quality than Light keeps.
        var input = _dir.Combine("tiny.mp4");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.SmallClipAsync(), "-c:v", "libx264", "-preset", "medium", "-crf", "45", "-c:a", "aac", "-b:a", "32k",
            "-f", "mp4", input);

        var e = await Assert.ThrowsAsync<OperationSkippedException>(() => Op("video.compress-light").RunAsync(input, CancellationToken.None));

        Assert.Equal(VideoCompress.NotSmallerMessage, e.Message);
        Assert.Equal(["tiny.mp4"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_minute_of_1080p_under_25_mb_is_less_than_25_000_000_bytes_and_keeps_its_length()
    {
        var input = _dir.Combine("minute.mp4");
        await TestFfmpeg.RunRawAsync(
            "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=60", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=60",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", "-f", "mp4", input);
        Assert.True(new FileInfo(input).Length > 25_000_000, $"the input is only {new FileInfo(input).Length} bytes");
        var passes = _dir.Combine("passes");
        var reports = new List<int>();

        var output = await Under(25, passes).RunAsync(input, new SyncProgress(report => reports.Add(report.Position)), CancellationToken.None);

        Assert.Equal(_dir.Combine("minute (under 25 MB).mp4"), output);
        Assert.True(new FileInfo(output).Length < 25_000_000, $"{new FileInfo(output).Length} bytes");
        var probe = await Probe(output);
        Assert.Equal(["h264", "aac"], probe.Streams.Select(s => s.Codec));
        Assert.Equal(60, probe.Duration, 1);
        // Two passes, each half of the bar; nothing of x264's statistics is left.
        Assert.Contains(reports, percent => percent is > 0 and < 50);
        Assert.Contains(reports, percent => percent is > 50 and < 100);
        Assert.Equal(100, reports[^1]);
        Assert.Empty(Directory.GetFileSystemEntries(passes));
    }

    [Fact]
    public async Task A_phone_video_of_variable_frame_rate_fits_with_every_frame_and_its_times()
    {
        // Without the same frame-time mode in both passes the second pass added frames the first never saw, and with
        // ffmpeg 6.1 it hung (code review, CR-3).
        var input = await Fixtures.CopyAsync(Fixtures.VariableRateClipAsync(), _dir, "screen.mp4");
        var limit = new FileInfo(input).Length / 3;

        var output = await Under(10, _dir.Combine("passes"), limit).RunAsync(input, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));

        Assert.True(new FileInfo(output).Length < limit, $"{new FileInfo(output).Length} of {limit} bytes");
        Assert.Equal(108, await Frames(output));
    }

    [Fact]
    public async Task Recordings_joined_byte_by_byte_fit_though_ffprobe_reads_the_first_one()
    {
        // ffprobe reads 20 s of these 60 (RC-1): the second pass's rate came from 20 s, three times too high, and three
        // encodes later the user was told it could not fit (code review, 2026-10-21).
        var piece = _dir.Combine("piece.ts");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=20", "-f", "lavfi", "-i", "sine=duration=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-f", "mpegts", piece);
        var input = _dir.Combine("joined.mts");
        var bytes = await File.ReadAllBytesAsync(piece);
        await File.WriteAllBytesAsync(input, [.. bytes, .. bytes, .. bytes]);
        var limit = new FileInfo(input).Length * 6 / 10;

        var output = await Under(10, _dir.Combine("passes"), limit).RunAsync(input, CancellationToken.None);

        Assert.True(new FileInfo(output).Length < limit, $"{new FileInfo(output).Length} of {limit} bytes");
        Assert.True((await Probe(output)).Duration > 55, $"{(await Probe(output)).Duration} s of 60");
    }

    [Fact]
    public async Task An_output_still_too_large_is_encoded_again_lower_twice_at_most()
    {
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mp4");
        var limit = new FileInfo(input).Length / 3;
        var rates = new List<string>();
        // The first two second passes come out at the limit (as if x264 overshot); the third as x264 made it.
        var overshoot = Overshooting(limit, rates, times: 2);

        var output = await Under(10, _dir.Combine("passes"), limit, overshoot).RunAsync(input, CancellationToken.None);

        Assert.True(new FileInfo(output).Length < limit);
        Assert.Equal(3, rates.Count);
        var kbps = rates.Select(rate => int.Parse(rate.TrimEnd('k'), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal([kbps[0], (int)(kbps[0] * 0.9), (int)(kbps[0] * 0.9 * 0.9)], kbps);
    }

    [Fact]
    public async Task An_output_that_never_fits_fails_after_two_retries_and_nothing_is_written()
    {
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mp4");
        var limit = new FileInfo(input).Length / 3;
        var rates = new List<string>();
        var op = Under(10, _dir.Combine("passes"), limit, Overshooting(limit, rates, times: int.MaxValue));

        var e = await Assert.ThrowsAsync<MediaException>(() => op.RunAsync(input, CancellationToken.None));

        Assert.Equal(op.CouldNotFitMessage, e.Message);
        Assert.Equal(3, rates.Count);
        Assert.Equal(["clip.mp4"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    // TestFfmpeg, but the first second passes write an output of exactly the limit, which EncodeAsync refuses as reached.
    private static Ffmpeg Overshooting(long limit, List<string> rates, int times) =>
        new(TestFfmpeg.Instance.FfmpegPath, TestFfmpeg.Instance.FfprobePath, async (tool, arguments, timeout, line, ct) =>
        {
            var run = await TestFfmpeg.Instance.Run(tool, arguments, timeout, line, ct);
            var at = arguments.ToList().IndexOf("-pass");
            if (at >= 0 && arguments[at + 1] == "2")
            {
                rates.Add(arguments[arguments.ToList().IndexOf("-b:v") + 1]);
                if (rates.Count <= times)
                {
                    await using var output = File.OpenWrite(arguments[^1]["file:".Length..]);
                    output.SetLength(limit);
                }
            }

            return run;
        });

    private static async Task<int> Frames(string path) => int.Parse(
        (await TestFfmpeg.ProbeRawAsync("-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", path)).Trim(),
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Twenty_minutes_cannot_fit_under_10_mb_and_nothing_is_written()
    {
        // A tiny picture and 211 MB of PCM sound: written in a moment, far over 10 MB, 20 minutes long.
        var input = _dir.Combine("lecture.mkv");
        await TestFfmpeg.RunRawAsync(
            "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=5:duration=1200", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=1200",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_s16le", "-ac", "2", "-f", "matroska", input);
        var passes = _dir.Combine("passes");

        var e = await Assert.ThrowsAsync<MediaException>(() => Under(10, passes).RunAsync(input, CancellationToken.None));

        Assert.StartsWith("Too long to fit under 10 MB", e.Message, StringComparison.Ordinal);
        Assert.Equal(["lecture.mkv"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
        Assert.False(Directory.Exists(passes) && Directory.GetFileSystemEntries(passes).Length > 0);
    }

    [Fact]
    public async Task A_video_already_under_the_size_is_skipped()
    {
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mp4");

        var e = await Assert.ThrowsAsync<OperationSkippedException>(() => Op("video.compress-under-10mb").RunAsync(input, CancellationToken.None));

        Assert.Equal("This video is already under 10 MB.", e.Message);
    }

    [Theory]
    // limit MB, seconds, short side, sound → video kbit/s, audio kbit/s, short side scaled to (0: kept)
    [InlineData(25, 60, 1080, true, 3105, 128, 0)] // 25 MB × 8 × 0.97 / 60 s = 3233 kbit/s
    [InlineData(25, 120, 1080, true, 1488, 128, 720)]
    [InlineData(25, 300, 1080, true, 550, 96, 480)]
    [InlineData(10, 60, 1080, false, 1293, 0, 720)]
    [InlineData(100, 30, 2160, true, 25738, 128, 0)]
    [InlineData(100, 120, 2160, true, 6338, 128, 1440)]
    [InlineData(10, 600, 1080, true, 65, 64, -1)] // 129 kbit/s in all: too little for any picture
    [InlineData(10, 400, 360, true, 130, 64, 0)] // a small video stays small, however little is left
    [InlineData(10, 400, 1080, true, 130, 64, 480)]
    public void The_bitrate_is_what_the_limit_leaves_and_the_picture_shrinks_until_it_suffices(
        int megabytes, int seconds, int shortSide, bool sound, int videoKbps, int audioKbps, int scaledTo)
    {
        var budget = VideoCompressUnder.Plan(megabytes * 1_000_000L, TimeSpan.FromSeconds(seconds), shortSide, sound);

        if (scaledTo < 0)
        {
            Assert.Null(budget);
            return;
        }

        Assert.Equal((videoKbps, audioKbps, scaledTo == 0 ? null : scaledTo), (budget!.Value.VideoKbps, budget.Value.AudioKbps, budget.Value.MaxShortSide));
    }

    [Fact]
    public void X264_statistics_are_counted_for_the_disk_space_check()
    {
        var info = new MediaInfo(TimeSpan.FromMinutes(20), new VideoTrack(0, "h264", 1920, 1080, 0, false, 30), [], new Dictionary<string, string>(), null);

        // 36 000 frames of 120 × 68 blocks.
        Assert.Equal(36_000L * ((120 * 68 * 2) + 200), VideoCompressUnder.StatisticsBytes(info, (1920, 1080)));
    }

    private static VideoCompressUnder Under(int megabytes, string passes, long? limitBytes = null, Ffmpeg? ffmpeg = null) =>
        new(megabytes, ffmpeg ?? TestFfmpeg.Instance) { PassFolderRoot = passes, LimitBytes = limitBytes ?? megabytes * 1_000_000L };
}
