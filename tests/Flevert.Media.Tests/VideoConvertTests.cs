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
    public async Task An_hdr_video_is_tone_mapped_to_8_bit_bt709()
    {
        var input = await Fixtures.CopyAsync(Fixtures.HdrMovAsync(), _dir, "hdr.mov");

        var output = await Op("video.to-mp4").RunAsync(input, CancellationToken.None);

        var video = (await Probe(output)).Streams[0];
        Assert.Equal(("h264", "yuv420p", "bt709", "bt709"), (video.Codec, video.PixelFormat, video.Transfer, video.Primaries));
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
    public void The_output_size_is_the_picture_as_shown_capped_on_its_short_side_with_even_sides(
        int width, int height, int rotation, int? maxShortSide, int expectedWidth, int expectedHeight)
    {
        var video = new VideoTrack(0, "h264", width, height, rotation, false, 30);

        Assert.Equal((expectedWidth, expectedHeight), VideoEncoding.OutputSize(video, maxShortSide));
    }

    [Fact]
    public void Filters_scale_only_when_the_size_changes_and_tone_map_only_hdr()
    {
        var sdr = new VideoTrack(0, "h264", 1920, 1080, 0, false, 30);
        var hdr = sdr with { IsHdr = true };

        Assert.Equal(["-vf", "format=yuv420p"], VideoEncoding.Filters(sdr, (1920, 1080)));
        Assert.Equal(["-vf", "scale=1280:720:flags=lanczos,format=yuv420p"], VideoEncoding.Filters(sdr, (1280, 720)));
        Assert.Equal(["-vf", $"{VideoEncoding.ToneMap},format=yuv420p"], VideoEncoding.Filters(hdr, (1920, 1080)));
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
