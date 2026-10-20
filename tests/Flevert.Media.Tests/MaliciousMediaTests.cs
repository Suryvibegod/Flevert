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
