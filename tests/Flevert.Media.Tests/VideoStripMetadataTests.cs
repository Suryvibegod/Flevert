using System.Text.Json;
using Flevert.Core;
using static Flevert.Media.Tests.VideoConvertTests;

namespace Flevert.Media.Tests;

/// <summary>Remove hidden info for a video (docs/specs/media.md; success criterion 13): no re-encoding, no location.</summary>
public sealed class VideoStripMetadataTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op() => MediaOperations.Create(TestFfmpeg.Instance).OfType<IOperation>().Single(op => op.Id == "video.strip-metadata");

    [Fact]
    public void It_is_offered_for_every_video_type_on_its_own()
    {
        Assert.Equal("Remove hidden info", Op().MenuPath);
        Assert.Equal(MediaJob.VideoExtensions, Op().Extensions);
    }

    [Fact]
    public async Task A_phone_video_loses_where_it_was_filmed_and_keeps_its_date_its_rotation_its_pictures_and_its_sound()
    {
        var input = await Fixtures.CopyAsync(Fixtures.PhoneMovAsync(), _dir, "IMG_0042.MOV");

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("IMG_0042 (no metadata).MOV"), output);
        var json = await TestFfmpeg.ProbeRawAsync("-show_format", "-show_streams", "-of", "json", output);
        Assert.DoesNotContain("48.8577", json, StringComparison.Ordinal);
        Assert.DoesNotContain(TagNames(json), name => name.Contains("location", StringComparison.OrdinalIgnoreCase));
        var probe = await Probe(output);
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", probe.Format);
        Assert.StartsWith("2026-07-14T09:30:00", probe.Tags["creation_time"], StringComparison.Ordinal);
        // Not re-encoded, so still stored sideways and turned upright by the display matrix, as the original.
        var info = await TestFfmpeg.Instance.ProbeAsync(output, CancellationToken.None);
        Assert.Equal((640, 360, 360, 640), (info.Video!.Width, info.Video.Height, info.Video.DisplayWidth, info.Video.DisplayHeight));
        // H.264 loses its SEI messages, so its packets differ, but every picture decodes the same; the sound is copied byte
        // for byte.
        Assert.Equal(await PicturesHash(input), await PicturesHash(output));
        Assert.Equal(await PacketsHash(input, "a"), await PacketsHash(output, "a"));
    }

    [Fact]
    public async Task What_an_encoder_or_a_camcorder_writes_into_h264_itself_is_removed()
    {
        // x264 signs every stream it writes with an SEI message ("x264 - core … options: …"); AVCHD camcorders keep the
        // camera, the time and the place in the same kind of message (security review, SEC-2).
        var input = _dir.Combine("camcorder.m2ts");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.SmallClipAsync(), "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "ac3",
            "-mpegts_m2ts_mode", "1", "-f", "mpegts", input);
        Assert.Contains("x264 - core", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(input)), StringComparison.Ordinal);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.DoesNotContain("x264 - core", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output)), StringComparison.Ordinal);
        Assert.Equal("mpegts", (await Probe(output)).Format);
        Assert.Equal(await PicturesHash(input), await PicturesHash(output));
    }

    [Fact]
    public async Task A_camcorder_clip_remuxed_to_mp4_loses_what_its_h264_carries_too()
    {
        // AVCHD clips are often remuxed to MP4 or MKV, keeping MDPM with them (re-check review, RC2-1). The SEI is added
        // as camcorders write it: user data under MDPM's UUID, its payload starting "MDPM".
        var input = _dir.Combine("camcorder.mp4");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.SmallClipAsync(), "-c:v", "libx264", "-preset", "ultrafast",
            "-bsf:v", "h264_metadata=sei_user_data=17ee8c60f84d11d98cd60800200c9a66+MDPM+48.8577+002.2950", "-c:a", "copy", "-f", "mp4", input);
        Assert.Contains("MDPM+48.8577", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(input)), StringComparison.Ordinal);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var bytes = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output));
        Assert.DoesNotContain("MDPM", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("x264 - core", bytes, StringComparison.Ordinal);
        Assert.Equal(await PicturesHash(input), await PicturesHash(output));
    }

    [Theory]
    [InlineData(65_536 - 10, "17ee8c60f84d11d98cd60800200c9a66", true)] // across two reads
    [InlineData((8 * 1024 * 1024) - 100, "17ee8c60f84d11d98cd60800200c9a66", true)]
    [InlineData((8 * 1024 * 1024) + (128 * 1024), "17ee8c60f84d11d98cd60800200c9a66", false)] // past the first 8 MB
    [InlineData(1000, "", false)] // the four letters alone, as a picture may hold them by chance (RC3-3)
    public void A_camcorders_data_is_looked_for_in_the_first_8_mb_of_an_h264_file(int offset, string uuid, bool drops)
    {
        var path = _dir.Combine("clip.mp4");
        var bytes = new byte[offset + 1000];
        byte[] data = [.. Convert.FromHexString(uuid), .. "MDPM"u8];
        data.CopyTo(bytes, offset);
        File.WriteAllBytes(path, bytes);

        Assert.Equal(drops, VideoStripMetadata.DropsSei(path, ".mp4", new VideoTrack(0, "h264", 1920, 1080, 0, false, 50)));
        Assert.False(VideoStripMetadata.DropsSei(path, ".mp4", new VideoTrack(0, "hevc", 1920, 1080, 0, false, 50)));
    }

    [Fact]
    public async Task A_stream_seeked_by_its_recovery_points_keeps_its_sei()
    {
        // Intra refresh, as dashcams write it: one keyframe, then recovery points in SEI. Dropping them would leave nothing
        // to seek by (re-check review, RC-3), so such a stream is copied whole.
        var input = _dir.Combine("dashcam.mp4");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=15", "-c:v", "libx264", "-preset", "ultrafast",
            "-x264-params", "intra-refresh=1:keyint=60", "-metadata", "title=Route", "-f", "mp4", input);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Contains("x264 - core", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output)), StringComparison.Ordinal);
        Assert.Equal(await PacketsHash(input, "v"), await PacketsHash(output, "v"));
    }

    [Fact]
    public async Task The_first_sound_track_is_kept_with_its_language()
    {
        var input = await Fixtures.CopyAsync(Fixtures.TwoAudioMkvAsync(), _dir, "film.mkv");

        var output = await Op().RunAsync(input, CancellationToken.None);

        var probe = await Probe(output);
        Assert.Equal(["vp9", "opus"], probe.Streams.Select(s => s.Codec));
        Assert.Equal("eng", probe.Streams[1].Language);
    }

    [Fact]
    public async Task Subtitles_attachments_and_other_streams_are_left_out()
    {
        var input = _dir.Combine("film.mkv");
        var subtitles = _dir.File("film.srt", "1\n00:00:00,000 --> 00:00:02,000\nXin chào\n");
        var font = _dir.File("font.ttf", "not really a font");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.TwoAudioMkvAsync(), "-i", subtitles, "-map", "0", "-map", "1", "-c", "copy", "-c:s", "srt",
            "-attach", font, "-metadata:s:t", "mimetype=font/ttf", "-f", "matroska", input);
        File.Delete(subtitles);
        File.Delete(font);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var probe = await Probe(output);
        Assert.Equal(["vp9", "opus"], probe.Streams.Select(s => s.Codec));
        // VP9 has no SEI: its packets are copied byte for byte.
        Assert.Equal(await PacketsHash(input, "v"), await PacketsHash(output, "v"));
    }

    [Fact]
    public async Task A_video_known_to_hide_nothing_is_skipped_and_nothing_is_written()
    {
        // VP9 in WEBM with no tags but what Matroska writes itself; an MP4 or an H.264 stream always gets its copy (DD-9).
        var input = _dir.Combine("clean.webm");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.TwoAudioMkvAsync(), "-map", "0:v", "-map", "0:a:0", "-map_metadata", "-1", "-c", "copy",
            "-f", "webm", input);

        var e = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(VideoStripMetadata.NothingToRemoveMessage, e.Message);
        Assert.Equal(["clean.webm"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(".mp4", "mp4", "mov,mp4,m4a,3gp,3g2,mj2")]
    [InlineData(".webm", "webm", "matroska,webm")]
    [InlineData(".avi", "avi", "avi")]
    [InlineData(".wmv", "asf", "asf")]
    [InlineData(".3gp", "3gp", "mov,mp4,m4a,3gp,3g2,mj2")]
    public async Task Each_container_is_written_again_as_itself(string extension, string muxer, string format)
    {
        var input = _dir.Combine("clip" + extension);
        string[] codecs = extension switch
        {
            ".webm" => ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-c:a", "libopus"],
            ".wmv" => ["-c:v", "wmv2", "-c:a", "wmav2"],
            _ => ["-c:v", "libx264", "-preset", "ultrafast", "-c:a", extension == ".avi" ? "libmp3lame" : "aac"],
        };
        await TestFfmpeg.RunRawAsync(["-i", await Fixtures.SmallClipAsync(), .. codecs, "-metadata", "title=Secret place", "-f", muxer, input]);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var probe = await Probe(output);
        Assert.Equal(format, probe.Format);
        Assert.DoesNotContain("Secret place", await TestFfmpeg.ProbeRawAsync("-show_format", "-show_streams", "-of", "json", output), StringComparison.Ordinal);
        Assert.Equal(await PicturesHash(input), await PicturesHash(output));
    }

    [Theory]
    [InlineData(".webm", "vp9", """{ "major_brand": "isom", "encoder": "Lavf61.7.100", "creation_time": "2026-01-01T00:00:00Z" }""", """{ "language": "und", "handler_name": "VideoHandler" }""", "", false)]
    [InlineData(".webm", "vp9", """{ "com.apple.quicktime.model": "iPhone 16" }""", "{}", "", true)]
    [InlineData(".webm", "vp9", "{}", """{ "location": "+1+2/" }""", "", true)]
    [InlineData(".webm", "vp9", "{}", "{}", """, { "index": 1, "codec_type": "data" }""", true)]
    [InlineData(".webm", "vp9", "{}", "{}", """, { "index": 1, "codec_type": "audio" }, { "index": 2, "codec_type": "audio" }""", true)]
    [InlineData(".mkv", "h264", "{}", "{}", "", true)] // SEI
    [InlineData(".mp4", "hevc", "{}", "{}", "", true)] // XMP, makers' boxes
    [InlineData(".mts", "mpeg2video", "{}", "{}", "", true)] // AVCHD
    public void Hidden_info_is_a_tag_a_container_does_not_write_itself_a_stream_left_out_or_a_place_ffprobe_cannot_see(
        string extension, string codec, string formatTags, string videoTags, string more, bool hidden)
    {
        var info = MediaInfo.Parse($$"""
            { "streams": [ { "index": 0, "codec_type": "video", "codec_name": "{{codec}}", "width": 64, "height": 64, "tags": {{videoTags}} }{{more}} ],
              "format": { "duration": "1", "tags": {{formatTags}} } }
            """);

        Assert.Equal(hidden, VideoStripMetadata.MayHoldHiddenInfo(info, extension));
    }

    // The MD5 of the decoded pictures: equal when nothing that shows was changed.
    private async Task<string> PicturesHash(string path)
    {
        var output = _dir.Combine(Guid.NewGuid().ToString("N") + ".md5");
        await TestFfmpeg.RunRawAsync("-i", path, "-map", "0:v:0", "-f", "md5", output);
        var hash = (await File.ReadAllTextAsync(output)).Trim();
        File.Delete(output);
        return hash;
    }

    // The MD5 of a stream's packets as stored: equal only when they were copied, not encoded again.
    private async Task<string> PacketsHash(string path, string stream)
    {
        var output = _dir.Combine(Guid.NewGuid().ToString("N") + ".md5");
        await TestFfmpeg.RunRawAsync("-i", path, "-map", $"0:{stream}:0", "-c", "copy", "-f", "md5", output);
        var hash = (await File.ReadAllTextAsync(output)).Trim();
        File.Delete(output);
        return hash;
    }

    private static List<string> TagNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("streams").EnumerateArray().Prepend(document.RootElement.GetProperty("format"))
            .Where(holder => holder.TryGetProperty("tags", out _))
            .SelectMany(holder => holder.GetProperty("tags").EnumerateObject().Select(tag => tag.Name))];
    }
}
