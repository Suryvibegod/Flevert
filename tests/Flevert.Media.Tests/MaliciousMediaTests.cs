using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Flevert.Core;

namespace Flevert.Media.Tests;

/// <summary>
/// Files made to hurt a video tool (docs/specs/media.md, "Bảo mật" and success criteria 5 and 10): each is refused or
/// converted within 10 s, never makes ffmpeg connect anywhere or read another file, and leaves no partial output.
/// </summary>
public sealed class MaliciousMediaTests : IDisposable
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);
    private readonly TempDir _dir = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public MaliciousMediaTests() => _listener.Start();

    public void Dispose()
    {
        _listener.Stop();
        _listener.Dispose();
        _dir.Dispose();
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private static IOperation Op(string id) => MediaOperations.Create(TestFfmpeg.Instance).OfType<IOperation>().Single(op => op.Id == id);

    [Theory]
    [InlineData("video.to-mp4")]
    [InlineData("video.extract-mp3")]
    [InlineData("video.compress-light")]
    [InlineData("video.to-gif")]
    public async Task An_hls_playlist_named_mp4_is_refused_and_nothing_connects(string id)
    {
        var input = _dir.File("holiday.mp4", $"#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nhttp://127.0.0.1:{Port}/segment.ts\n#EXT-X-ENDLIST\n");

        var e = await Refused(id, input);

        Assert.Equal(MediaException.UnreadableMessage, e.Message);
    }

    [Fact]
    public async Task A_concat_list_named_mkv_is_refused_though_the_video_it_names_is_real()
    {
        // Only a plain name: ffmpeg's concat demuxer itself refuses a URL in its list (safe=1), not the file beside it.
        await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "real.mp4");
        var input = _dir.File("film.mkv", "ffconcat version 1.0\nfile real.mp4\n");

        var e = await Refused("video.to-mp4", input, keep: "real.mp4");

        Assert.Equal(MediaException.UnreadableMessage, e.Message);
    }

    [Theory]
    [InlineData("holiday.mp4", "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nprivate.wav\n#EXT-X-ENDLIST\n")]
    [InlineData("holiday.mkv", "ffconcat version 1.0\nfile private.wav\n")]
    [InlineData("holiday.webm", "ffconcat version 1.0\nfile 'file:private.wav'\n")]
    public async Task A_playlist_naming_another_local_recording_is_refused_and_nothing_of_it_is_written(string name, string content)
    {
        // The known way to make ffmpeg leak a local file: a list that names it, so it lands in the video the user sends
        // on (success criterion 14). Here it is a recording ffmpeg reads: without the demuxer whitelist the concat list of
        // a plain name converts it. ffmpeg's own checks also stop the other two (an HLS playlist not named .m3u8, a URL in
        // a concat list); the whitelist does not rely on them.
        await Fixtures.CopyAsync(Fixtures.WavAsync(), _dir, "private.wav");
        var input = _dir.File(name, content);

        foreach (var id in (string[])["video.to-mp4", "video.extract-mp3", "video.strip-metadata"])
            Assert.Equal(MediaException.UnreadableMessage, (await Refused(id, input, keep: "private.wav")).Message);
    }

    [Fact]
    public async Task A_video_claiming_100_000_pixels_across_is_refused_before_anything_is_decoded()
    {
        // A raw-video AVI of 16 × 16 pixels whose headers say 100 000 × 16.
        var input = _dir.Combine("wide.avi");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "testsrc2=size=16x16:rate=5:duration=1", "-c:v", "rawvideo", "-pix_fmt", "bgr24", "-f", "avi", input);
        var bytes = await File.ReadAllBytesAsync(input);
        var header = bytes.AsSpan().IndexOf("avih"u8);
        BitConverter.TryWriteBytes(bytes.AsSpan(header + 8 + 32), 100_000);
        var format = bytes.AsSpan().IndexOf("strf"u8);
        BitConverter.TryWriteBytes(bytes.AsSpan(format + 8 + 4), 100_000);
        await File.WriteAllBytesAsync(input, bytes);

        var e = await Refused("video.to-mp4", input);

        Assert.Equal(MediaException.TooLargeVideoMessage, e.Message);
    }

    [Fact]
    public async Task Subtitles_and_attachments_never_reach_a_converted_video()
    {
        var input = _dir.Combine("film.mkv");
        var subtitles = _dir.File("film.srt", "1\n00:00:00,000 --> 00:00:02,000\nXin chào\n");
        var font = _dir.File("font.ttf", "not really a font");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.SmallClipAsync(), "-i", subtitles, "-map", "0", "-map", "1", "-c", "copy", "-c:s", "srt",
            "-attach", font, "-metadata:s:t", "mimetype=font/ttf", "-f", "matroska", input);
        File.Delete(subtitles);
        File.Delete(font);

        var output = await Op("video.to-mp4").RunAsync(input, CancellationToken.None);

        Assert.Equal(["h264", "aac"], (await VideoConvertTests.Probe(output)).Streams.Select(s => s.Codec));
    }

    [Theory]
    [InlineData("empty.mp4", "")]
    [InlineData("notes.avi", "Shopping list: milk, eggs.\n")]
    [InlineData("script.webm", "#!/bin/sh\nrm -rf ~\n")]
    public async Task A_file_that_is_no_video_at_all_is_refused(string name, string content)
    {
        var input = _dir.File(name, content);

        var e = await Refused("video.to-mp4", input);

        Assert.Equal(MediaException.UnreadableMessage, e.Message);
    }

    [Fact]
    public async Task A_video_cut_off_before_its_index_is_refused()
    {
        var input = _dir.Combine("cut.mov");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.SmallClipAsync(), "-c", "copy", "-f", "mov", input);
        var bytes = await File.ReadAllBytesAsync(input);
        await File.WriteAllBytesAsync(input, bytes[..(bytes.Length / 3)]); // MOV keeps its index (moov) at the end

        var e = await Refused("video.to-mp4", input);

        Assert.Equal(MediaException.UnreadableMessage, e.Message);
    }

    [Fact]
    public async Task A_stream_whose_times_claim_99_hours_is_refused_before_anything_is_encoded()
    {
        // Two MPEG-TS pieces of a second each, the second stamped 99 hours later: ffprobe reads 99 hours from the times.
        var first = _dir.Combine("a.ts");
        var second = _dir.Combine("b.ts");
        string[] piece = ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=1", "-c:v", "libx264", "-preset", "ultrafast"];
        await TestFfmpeg.RunRawAsync([.. piece, "-f", "mpegts", first]);
        await TestFfmpeg.RunRawAsync([.. piece, "-output_ts_offset", "356400", "-f", "mpegts", second]);
        var input = _dir.Combine("camera.mts");
        await File.WriteAllBytesAsync(input, [.. await File.ReadAllBytesAsync(first), .. await File.ReadAllBytesAsync(second)]);
        File.Delete(first);
        File.Delete(second);

        var e = await Refused("video.to-mp4", input);

        Assert.Equal(MediaException.TooLongMessage, e.Message);
    }

    [Fact]
    public async Task A_sound_file_holding_far_more_than_its_header_claims_converts_whole_within_the_size_limit()
    {
        // A FLAC whose STREAMINFO says 2 s over 60 s of sound: ffprobe reads 2 s. Sound is never cut by time (a raw AAC's
        // length is only an estimate, RC-1); -fs, at most the free disk, bounds what it can write (SEC-1).
        var input = _dir.Combine("short.flac");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=60", "-c:a", "flac", "-f", "flac", input);
        var bytes = await File.ReadAllBytesAsync(input);
        // "fLaC", a block header, then STREAMINFO: 10 bytes of block and frame sizes, then 64 bits of sample rate (20),
        // channels - 1 (3), bits - 1 (5) and the total number of samples (36).
        var packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(18));
        packed = (packed & ~((1UL << 36) - 1)) | (44100UL * 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18), packed);
        await File.WriteAllBytesAsync(input, bytes);
        Assert.Equal(2, (await TestFfmpeg.Instance.ProbeAsync(input, CancellationToken.None)).Duration.TotalSeconds, 1);

        var output = await Op("audio.to-wav").RunAsync(input, CancellationToken.None);

        Assert.Equal(60, (await TestFfmpeg.Instance.ProbeAsync(output, CancellationToken.None)).Duration.TotalSeconds, 0);
    }

    [Fact]
    public async Task A_video_encode_stopped_at_its_time_bound_fails_rather_than_pass_a_cut_output_for_the_whole()
    {
        // ffmpeg says it got as far as the bound (as it does when -t stops it): the file held more than it claimed.
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "clip.mp4");
        var output = _dir.Combine("out.partial");
        var bound = Ffmpeg.MaxTime(TimeSpan.FromSeconds(4), new FileInfo(input).Length);
        var stopped = new Ffmpeg(TestFfmpeg.Instance.FfmpegPath, TestFfmpeg.Instance.FfprobePath, async (tool, arguments, timeout, line, ct) =>
        {
            var run = await TestFfmpeg.Instance.Run(tool, arguments, timeout, line, ct);
            line?.Invoke($"out_time_us={(long)bound.TotalMicroseconds}");
            return run;
        });

        var e = await Assert.ThrowsAsync<MediaException>(() => stopped.EncodeAsync(
            input, ["-c:v", "libx264", "-preset", "ultrafast"], output, "mp4", TimeSpan.FromSeconds(4), 100_000_000, null, CancellationToken.None,
            boundTime: true));

        Assert.Equal(MediaException.LongerThanItSaysMessage, e.Message);
    }

    [Fact]
    public async Task A_video_damaged_in_the_middle_converts_or_fails_clearly_within_10_seconds()
    {
        var input = _dir.Combine("damaged.mkv");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.SmallClipAsync(), "-c", "copy", "-f", "matroska", input);
        var bytes = await File.ReadAllBytesAsync(input);
        new Random(42).NextBytes(bytes.AsSpan(bytes.Length / 3, bytes.Length / 3));
        await File.WriteAllBytesAsync(input, bytes);

        var stopwatch = Stopwatch.StartNew();
        string? output = null;
        try
        {
            output = await Op("video.to-mp4").RunAsync(input, CancellationToken.None);
        }
        catch (MediaException)
        {
        }

        Assert.True(stopwatch.Elapsed < Quick, $"{stopwatch.Elapsed}");
        Assert.Equal(output is null ? ["damaged.mkv"] : ["damaged.mkv", "damaged.mp4"],
            Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(_listener.Pending());
    }

    // Runs the operation on a file it must refuse: quickly, writing nothing, connecting nowhere.
    private async Task<MediaException> Refused(string id, string input, string? keep = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<MediaException>(() => Op(id).RunAsync(input, CancellationToken.None));

        Assert.True(stopwatch.Elapsed < Quick, $"{stopwatch.Elapsed}");
        Assert.Equal(new[] { Path.GetFileName(input), keep }.OfType<string>().Order(StringComparer.Ordinal),
            Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(_listener.Pending(), "something connected to the test server");
        return e;
    }
}
