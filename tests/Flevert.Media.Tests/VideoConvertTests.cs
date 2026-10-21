using System.Text.Json;
using Flevert.Core;

namespace Flevert.Media.Tests;

/// <summary>Convert to &gt; MP4, MOV, WEBM (docs/specs/media.md; success criterion 1).</summary>
public sealed class VideoConvertTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string id) => MediaOperations.Create(TestFfmpeg.Instance).OfType<IOperation>().Single(op => op.Id == id);

    [Fact]
    public void Convert_offers_each_video_type_except_the_file_s_own_one_at_a_time()
    {
        var convert = MediaOperations.Create(TestFfmpeg.Instance).OfType<IOperation>()
            .Where(op => op.Id.StartsWith("video.to-", StringComparison.Ordinal) && op.Id != "video.to-gif").ToList();

        // Not MKV: few people need it as an output; it stays a source (docs/specs/media.md).
        Assert.Equal(["Convert to/MP4", "Convert to/MOV", "Convert to/WEBM"], convert.Select(op => op.MenuPath));
        Assert.All(convert, op => Assert.Equal(1, op.MaxParallel));
        Assert.DoesNotContain(".mp4", convert[0].Extensions);
        Assert.Contains(".m4v", convert[0].Extensions);
        Assert.DoesNotContain(".mov", convert[1].Extensions);
        Assert.DoesNotContain(".webm", convert[2].Extensions);
        Assert.All(convert, op => Assert.Contains(".mkv", op.Extensions));
        Assert.All(convert, op => Assert.Equal(MediaJob.VideoExtensions.Length - 1, op.Extensions.Count));
    }

    [Theory]
    [InlineData("video.to-mov", ".mov", "mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac")]
    [InlineData("video.to-webm", ".webm", "matroska,webm", "vp9", "opus")]
    public async Task A_clip_converts_to_each_type_at_its_size_and_length(string id, string extension, string format, string videoCodec, string audioCodec)
    {
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mp4");

        var output = await Op(id).RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("clip" + extension), output);
        var probe = await Probe(output);
        Assert.Equal(format, probe.Format);
        Assert.Equal([videoCodec, audioCodec], probe.Streams.Select(s => s.Codec));
        Assert.Equal((640, 360, "yuv420p"), (probe.Streams[0].Width, probe.Streams[0].Height, probe.Streams[0].PixelFormat));
        Assert.Equal(4, probe.Duration, 1);
        Assert.Equal("Small", probe.Tags["title"]);
    }

    [Fact]
    public async Task A_phone_video_becomes_an_upright_mp4_with_its_date_and_without_where_it_was_filmed()
    {
        var input = await Fixtures.CopyAsync(Fixtures.PhoneMovAsync(), _dir, "IMG_0042.MOV");
        Assert.Contains("48.8577", await TestFfmpeg.ProbeRawAsync("-show_format", "-show_streams", "-of", "json", input), StringComparison.Ordinal);

        var output = await Op("video.to-mp4").RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("IMG_0042.mp4"), output);
        var json = await TestFfmpeg.ProbeRawAsync("-show_format", "-show_streams", "-of", "json", output);
        Assert.DoesNotContain("48.8577", json, StringComparison.Ordinal);
        Assert.DoesNotContain(TagNames(json), name => name.Contains("location", StringComparison.OrdinalIgnoreCase));
        var probe = await Probe(output);
        // Turned upright in the pixels, so no player needs the display matrix, and none is left to turn it twice.
        Assert.Equal((360, 640), (probe.Streams[0].Width, probe.Streams[0].Height));
        Assert.DoesNotContain("Display Matrix", json, StringComparison.Ordinal);
        Assert.StartsWith("2026-07-14T09:30:00", probe.Tags["creation_time"], StringComparison.Ordinal);
        Assert.Equal(["h264", "aac"], probe.Streams.Select(s => s.Codec));
    }

    [Fact]
    public async Task A_video_of_variable_frame_rate_keeps_its_frames_rather_than_gaining_copies()
    {
        var input = await Fixtures.CopyAsync(Fixtures.VariableRateClipAsync(), _dir, "screen.mp4");

        var output = await Op("video.to-mov").RunAsync(input, CancellationToken.None);

        var frames = await TestFfmpeg.ProbeRawAsync("-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", output);
        Assert.Equal("108", frames.Trim());
    }

    [Theory]
    [InlineData("pq")]
    [InlineData("hlg")]
    public async Task An_hdr_video_is_tone_mapped_to_8_bit_bt709(string transfer)
    {
        // HDR10 (PQ) as cameras write it, HLG as iPhones record it. Only the tags are checked here; how the colours look
        // is checked by eye on a real iPhone video (docs/specs/media.md, criterion 1).
        var input = await Fixtures.CopyAsync(transfer == "pq" ? Fixtures.HdrMovAsync() : Fixtures.HlgMovAsync(), _dir, "hdr.mov");

        var output = await Op("video.to-mp4").RunAsync(input, CancellationToken.None);

        var video = (await Probe(output)).Streams[0];
        Assert.Equal(("h264", "yuv420p", "bt709", "bt709"), (video.Codec, video.PixelFormat, video.Transfer, video.Primaries));
    }

    [Theory]
    [InlineData("arib-std-b67")]
    [InlineData("smpte2084")]
    public async Task An_hdr_video_keeps_its_brightness_once_tone_mapped(string transfer)
    {
        // An SDR picture made HDR at HDR's reference white (203 nits) and mapped back by Flevert: Hable made it a fifth
        // darker (mean luma 103 of 126), Mobius keeps it within a few percent (doubt review, 2026-10-21).
        var sdr = _dir.Combine("sdr.mp4");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", sdr);
        var input = _dir.Combine("hdr.mov");
        await TestFfmpeg.RunRawAsync("-i", sdr, "-vf", $"zscale=tin=bt709:t={transfer}:pin=bt709:p=bt2020:min=bt709:m=bt2020nc:npl=203,format=yuv420p10le",
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-color_primaries", "bt2020", "-color_trc", transfer,
            "-colorspace", "bt2020nc", input);

        var output = await Op("video.to-mp4").RunAsync(input, CancellationToken.None);

        var (before, after) = (await MeanLuma(sdr), await MeanLuma(output));
        Assert.InRange(after, before * 0.92, before * 1.08);
    }

    [Fact]
    public void A_picture_larger_than_4k_is_decoded_and_encoded_with_fewer_threads()
    {
        var k8 = new VideoTrack(0, "hevc", 7680, 4320, 0, true, 30);
        var k4 = k8 with { Width = 3840, Height = 2160 };

        Assert.Equal(["-threads", "2"], VideoEncoding.DecoderThreadsFor(k8));
        Assert.Equal(["-threads", "6"], VideoEncoding.EncoderThreadsFor(k8));
        Assert.Equal(VideoEncoding.DecoderThreads, VideoEncoding.DecoderThreadsFor(k4));
        Assert.Empty(VideoEncoding.EncoderThreadsFor(k4));
    }

    [Theory]
    [InlineData("video.to-mp4")]
    [InlineData("video.to-webm")]
    [InlineData("video.compress-balanced")]
    public async Task An_8k_video_s_encoder_gets_6_threads_last_and_its_decoder_2(string id)
    {
        // ffmpeg takes the last -threads of a stream: the 8K memory fix holds only while it comes after the codec's own.
        var calls = new List<IReadOnlyList<string>>();
        var eightK = new Ffmpeg(TestFfmpeg.Instance.FfmpegPath, TestFfmpeg.Instance.FfprobePath, async (tool, arguments, timeout, line, ct) =>
        {
            var run = await TestFfmpeg.Instance.Run(tool, arguments, timeout, line, ct);
            if (tool == TestFfmpeg.Instance.FfprobePath)
            {
                var json = System.Text.RegularExpressions.Regex.Replace(run.StandardOutput, @"""width"": \d+", @"""width"": 7680");
                return run with { StandardOutput = System.Text.RegularExpressions.Regex.Replace(json, @"""height"": \d+", @"""height"": 4320") };
            }

            calls.Add(arguments);
            return new ExternalToolResult(1, "", "stopped by the test", false, false);
        });
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mkv");

        await Assert.ThrowsAsync<MediaException>(() => MediaOperations.Create(eightK).OfType<IOperation>().Single(op => op.Id == id).RunAsync(input, CancellationToken.None));

        var arguments = calls[0].ToList();
        var at = arguments.IndexOf("-i");
        Assert.Equal("2", arguments[arguments.LastIndexOf("-threads", at) + 1]);
        Assert.Equal("6", arguments[arguments.LastIndexOf("-threads") + 1]);
        Assert.Contains("scale=3840:2160", arguments[arguments.IndexOf("-vf") + 1], StringComparison.Ordinal);
    }

    // The mean of the frames' average luma, as ffmpeg's signalstats measures it.
    private static async Task<double> MeanLuma(string path)
    {
        var run = await TestFfmpeg.Instance.Run(TestFfmpeg.Instance.FfmpegPath,
            ["-hide_banner", "-nostdin", "-i", path, "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-"],
            TimeSpan.FromMinutes(1), null, CancellationToken.None);
        var values = System.Text.RegularExpressions.Regex.Matches(run.StandardError, @"YAVG=([0-9.]+)")
            .Select(match => double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.NotEmpty(values);
        return values.Average();
    }

    [Fact]
    public async Task Only_the_first_video_and_the_first_sound_stream_are_kept_with_its_language()
    {
        var input = await Fixtures.CopyAsync(Fixtures.TwoAudioMkvAsync(), _dir, "film.mkv");

        var output = await Op("video.to-mp4").RunAsync(input, CancellationToken.None);

        var probe = await Probe(output);
        Assert.Equal(["h264", "aac"], probe.Streams.Select(s => s.Codec));
        Assert.Equal("eng", probe.Streams[1].Language);
        Assert.Equal(6, probe.Duration, 1);
    }

    [WindowsFact]
    public async Task The_notification_says_when_other_sound_tracks_were_left_out()
    {
        var input = await Fixtures.CopyAsync(Fixtures.TwoAudioMkvAsync(), _dir, "film.mkv");
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op("video.to-mp4"), [input], null, CancellationToken.None);

        Assert.Equal("Only the first of its 2 sound tracks was kept.", Assert.Single(outcome.Results).Note);
    }

    [Fact]
    public async Task A_video_without_sound_converts_and_a_file_without_video_is_refused()
    {
        var silent = _dir.Combine("silent.mkv");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.SmallClipAsync(), "-an", "-c", "copy", "-f", "matroska", silent);
        var audioOnly = _dir.Combine("song.mp4");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.WavAsync(), "-c:a", "aac", "-f", "mp4", audioOnly);

        var output = await Op("video.to-mp4").RunAsync(silent, CancellationToken.None);
        var e = await Assert.ThrowsAsync<MediaException>(() => Op("video.to-mov").RunAsync(audioOnly, CancellationToken.None));

        Assert.Equal(["h264"], (await Probe(output)).Streams.Select(s => s.Codec));
        Assert.Equal(MediaException.NoVideoMessage, e.Message);
    }

    [Fact]
    public async Task Reports_progress_as_a_rising_percentage()
    {
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mp4");
        var reports = new List<int>();

        await Op("video.to-mov").RunAsync(input, new SyncProgress(report => reports.Add(report.Position)), CancellationToken.None);

        Assert.Equal(100, reports[^1]);
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public async Task A_camcorder_s_interlaced_video_becomes_progressive()
    {
        // AVCHD 1080i: fields encoded as one frame show combs on motion (code review, 2026-10-21).
        var input = _dir.Combine("camcorder.mts");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=50:duration=2", "-vf", "tinterlace=interleave_top,fieldorder=tff",
            "-c:v", "libx264", "-preset", "ultrafast", "-flags", "+ildct+ilme", "-x264-params", "tff=1", "-f", "mpegts", input);
        Assert.True((await TestFfmpeg.Instance.ProbeAsync(input, CancellationToken.None)).Video!.IsInterlaced);

        var calls = new List<IReadOnlyList<string>>();
        var recording = new Ffmpeg(TestFfmpeg.Instance.FfmpegPath, TestFfmpeg.Instance.FfprobePath, (tool, arguments, timeout, line, ct) =>
        {
            if (tool == TestFfmpeg.Instance.FfmpegPath)
                calls.Add(arguments);
            return TestFfmpeg.Instance.Run(tool, arguments, timeout, line, ct);
        });

        // Through ffmpeg 8.1 as it ships, so the filter is known to run; the output's flag says progressive either way.
        var output = await MediaOperations.Create(recording).OfType<IOperation>().Single(op => op.Id == "video.to-mp4").RunAsync(input, CancellationToken.None);

        var filters = Assert.Single(calls).SkipWhile(argument => argument != "-vf").ElementAt(1);
        Assert.StartsWith(VideoEncoding.Deinterlace + ",", filters, StringComparison.Ordinal);
        Assert.False((await TestFfmpeg.Instance.ProbeAsync(output, CancellationToken.None)).Video!.IsInterlaced);
    }

    [Fact]
    public async Task A_film_with_5_1_sound_converts_to_webm()
    {
        // An MKV film's AC-3 5.1 decodes as 5.1(side), which libopus refused (code review, 2026-10-21).
        var input = _dir.Combine("film.mkv");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=2", "-f", "lavfi", "-i", "sine=duration=2",
            "-af", "aformat=channel_layouts=5.1(side)", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "ac3", "-f", "matroska", input);

        var output = await Op("video.to-webm").RunAsync(input, CancellationToken.None);

        Assert.Equal(6, (await TestFfmpeg.Instance.ProbeAsync(output, CancellationToken.None)).MainAudio!.Channels);
    }

    [Fact]
    public async Task Recordings_joined_byte_by_byte_convert_whole_though_ffprobe_reads_the_first_one()
    {
        // The usual way to join a camcorder's .mts pieces ("copy /b"): ffprobe reads the length from the first (RC-1).
        var piece = _dir.Combine("piece.ts");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=20", "-f", "lavfi", "-i", "sine=duration=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-f", "mpegts", piece);
        var input = _dir.Combine("joined.mts");
        var bytes = await File.ReadAllBytesAsync(piece);
        await File.WriteAllBytesAsync(input, [.. bytes, .. bytes, .. bytes]);
        File.Delete(piece);
        Assert.True((await TestFfmpeg.Instance.ProbeAsync(input, CancellationToken.None)).Duration < TimeSpan.FromSeconds(30));

        var output = await Op("video.to-mp4").RunAsync(input, CancellationToken.None);

        Assert.True((await Probe(output)).Duration > 55, $"{(await Probe(output)).Duration} s of 60");
    }

    [Theory]
    [InlineData(1920, 1080, 0, null, 1920, 1080)]
    [InlineData(1920, 1080, 90, null, 1080, 1920)]
    [InlineData(641, 361, 0, null, 640, 360)]
    [InlineData(3840, 2160, 0, 720, 1280, 720)]
    [InlineData(2160, 3840, 0, 720, 720, 1280)]
    [InlineData(3840, 2160, 270, 720, 720, 1280)]
    [InlineData(1280, 720, 0, 720, 1280, 720)]
    [InlineData(854, 480, 0, 720, 854, 480)]
    [InlineData(1920, 800, 0, 480, 1152, 480)]
    [InlineData(7680, 4320, 0, null, 3840, 2160)] // 8K is encoded at 4K, within the memory cap
    [InlineData(4320, 7680, 0, null, 2160, 3840)]
    [InlineData(3840, 2160, 0, null, 3840, 2160)]
    public void The_output_size_is_the_picture_as_shown_capped_on_its_short_side_with_even_sides(
        int width, int height, int rotation, int? maxShortSide, int expectedWidth, int expectedHeight)
    {
        var video = new VideoTrack(0, "h264", width, height, rotation, false, 30);

        Assert.Equal((expectedWidth, expectedHeight), VideoEncoding.OutputSize(video, maxShortSide));
    }

    [Theory]
    [InlineData(1920, 1080, 30, false, 1, 6)]
    [InlineData(1280, 720, 30, false, 1, 6)] // never below six times
    [InlineData(3840, 2160, 60, false, 1, 48)]
    [InlineData(3840, 2160, 60, true, 1, 96)]
    [InlineData(1920, 1080, 30, false, 2, 12)] // VP9
    [InlineData(8192, 4320, 239, true, 1, VideoEncoding.MaxSlowness)]
    public void A_pass_may_take_longer_for_more_pixels_a_second_and_for_hdr_within_bounds(
        int width, int height, double fps, bool hdr, double factor, double slowness)
    {
        Assert.Equal(slowness, VideoEncoding.Slowness(new VideoTrack(0, "h264", width, height, 0, hdr, fps), factor), 6);
    }

    [Fact]
    public void No_pass_may_take_longer_than_two_days() =>
        Assert.Equal(Ffmpeg.MaxTimeout, Ffmpeg.TimeoutFor(TimeSpan.FromHours(12), VideoEncoding.MaxSlowness));

    [Fact]
    public void A_video_encode_takes_in_half_as_much_again_as_ffprobe_read_or_what_its_bytes_hold_at_8_kbit_s()
    {
        Assert.Equal(TimeSpan.FromSeconds(70), Ffmpeg.MaxTime(TimeSpan.FromSeconds(40), 10_000));
        Assert.Equal(TimeSpan.FromSeconds(1000), Ffmpeg.MaxTime(TimeSpan.FromSeconds(40), 1_000_000));
    }

    [Theory]
    [InlineData("video.to-mp4", true, true)]
    [InlineData("video.to-webm", true, true)]
    [InlineData("video.compress-balanced", true, true)]
    [InlineData("video.to-gif", true, false)]
    [InlineData("video.strip-metadata", false, false)]
    [InlineData("video.extract-mp3", false, false)]
    public async Task Video_encodes_are_bounded_in_time_and_threads_and_copies_and_sound_are_not(string id, bool encodes, bool encoder)
    {
        var calls = new List<IReadOnlyList<string>>();
        var recording = new Ffmpeg(TestFfmpeg.Instance.FfmpegPath, TestFfmpeg.Instance.FfprobePath, (tool, arguments, timeout, line, ct) =>
        {
            if (tool == TestFfmpeg.Instance.FfmpegPath)
                calls.Add(arguments);
            return TestFfmpeg.Instance.Run(tool, arguments, timeout, line, ct);
        });
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mkv");

        await MediaOperations.Create(recording).OfType<IOperation>().Single(op => op.Id == id).RunAsync(input, CancellationToken.None);

        var arguments = Assert.Single(calls).ToList();
        var at = arguments.IndexOf("-i");
        Assert.Equal(encodes, arguments.Skip(at).Contains("-t"));
        Assert.Equal(encodes, arguments.Take(at).Contains("-threads"));
        Assert.Equal(encoder, arguments.Skip(at).Contains("-threads"));
    }

    [Fact]
    public void Filters_scale_only_when_the_size_changes_and_tone_map_only_hdr()
    {
        var sdr = new VideoTrack(0, "h264", 1920, 1080, 0, false, 30);
        var hdr = sdr with { IsHdr = true };

        Assert.Equal(["-vf", "format=yuv420p"], VideoEncoding.Filters(sdr, (1920, 1080)));
        Assert.Equal(["-vf", "scale=1280:720:flags=lanczos,format=yuv420p"], VideoEncoding.Filters(sdr, (1280, 720)));
        Assert.Equal(["-vf", $"{VideoEncoding.ToneMap},format=yuv420p"], VideoEncoding.Filters(hdr, (1920, 1080)));
        Assert.Equal(["-vf", $"{VideoEncoding.Deinterlace},scale=1280:720:flags=lanczos,format=yuv420p"],
            VideoEncoding.Filters(sdr with { IsInterlaced = true }, (1280, 720)));
    }

    // The names of the file's and every stream's tags.
    private static List<string> TagNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("streams").EnumerateArray().Prepend(document.RootElement.GetProperty("format"))
            .Where(holder => holder.TryGetProperty("tags", out _))
            .SelectMany(holder => holder.GetProperty("tags").EnumerateObject().Select(tag => tag.Name))];
    }

    internal sealed record StreamInfo(string Codec, int Width, int Height, string? PixelFormat, string? Transfer, string? Primaries, string? Language);

    internal static async Task<(string Format, IReadOnlyList<StreamInfo> Streams, double Duration, Dictionary<string, string> Tags)> Probe(string path)
    {
        using var json = JsonDocument.Parse(await TestFfmpeg.ProbeRawAsync("-show_format", "-show_streams", "-of", "json", path));
        var format = json.RootElement.GetProperty("format");
        var tags = format.TryGetProperty("tags", out var t)
            ? t.EnumerateObject().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.GetString()!)
            : [];
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().Select(s => new StreamInfo(
            s.GetProperty("codec_name").GetString()!,
            s.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            s.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
            s.TryGetProperty("pix_fmt", out var p) ? p.GetString() : null,
            s.TryGetProperty("color_transfer", out var c) ? c.GetString() : null,
            s.TryGetProperty("color_primaries", out var r) ? r.GetString() : null,
            s.TryGetProperty("tags", out var st) && st.TryGetProperty("language", out var l) ? l.GetString() : null)).ToList();
        return (format.GetProperty("format_name").GetString()!, streams,
            double.Parse(format.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), tags);
    }
}
