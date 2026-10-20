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
    public async Task A_phone_video_loses_where_it_was_filmed_and_keeps_its_date_its_rotation_and_every_byte_of_picture_and_sound()
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
        Assert.Equal(await PacketsHash(input, "v"), await PacketsHash(output, "v"));
        Assert.Equal(await PacketsHash(input, "a"), await PacketsHash(output, "a"));
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
        Assert.Equal(await PacketsHash(input, "v"), await PacketsHash(output, "v"));
    }

    [Fact]
    public async Task A_video_with_nothing_hidden_is_skipped_and_nothing_is_written()
    {
        var input = _dir.Combine("clean.mp4");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.SmallClipAsync(), "-map_metadata", "-1", "-c", "copy", "-f", "mp4", input);

        var e = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(VideoStripMetadata.NothingToRemoveMessage, e.Message);
        Assert.Equal(["clean.mp4"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(".mp4", "mp4", "mov,mp4,m4a,3gp,3g2,mj2")]
    [InlineData(".webm", "webm", "matroska,webm")]
    [InlineData(".avi", "avi", "avi")]
    public async Task Each_container_is_written_again_as_itself(string extension, string muxer, string format)
    {
        var input = _dir.Combine("clip" + extension);
        string[] codecs = extension == ".webm"
            ? ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-c:a", "libopus"]
            : ["-c:v", "libx264", "-preset", "ultrafast", "-c:a", extension == ".avi" ? "libmp3lame" : "aac"];
        await TestFfmpeg.RunRawAsync(["-i", await Fixtures.SmallClipAsync(), .. codecs, "-metadata", "title=Secret place", "-f", muxer, input]);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var probe = await Probe(output);
        Assert.Equal(format, probe.Format);
        Assert.DoesNotContain("Secret place", await TestFfmpeg.ProbeRawAsync("-show_format", "-show_streams", "-of", "json", output), StringComparison.Ordinal);
        Assert.Equal(await PacketsHash(input, "v"), await PacketsHash(output, "v"));
    }

    [Theory]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "video", "width": 64, "height": 64, "tags": { "language": "und", "handler_name": "VideoHandler", "vendor_id": "[0][0][0][0]" } } ], "format": { "duration": "1", "tags": { "major_brand": "isom", "minor_version": "512", "compatible_brands": "isomiso2avc1mp41", "encoder": "Lavf61.7.100", "creation_time": "2026-01-01T00:00:00Z" } } }""", false)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "video", "width": 64, "height": 64 } ], "format": { "duration": "1", "tags": { "com.apple.quicktime.model": "iPhone 16" } } }""", true)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "video", "width": 64, "height": 64, "tags": { "location": "+1+2/" } } ], "format": { "duration": "1" } }""", true)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "video", "width": 64, "height": 64 }, { "index": 1, "codec_type": "data" } ], "format": { "duration": "1" } }""", true)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "video", "width": 64, "height": 64 }, { "index": 1, "codec_type": "audio", "tags": { "title": "Commentary" } } ], "format": { "duration": "1" } }""", true)]
    public void Hidden_info_is_any_tag_a_container_does_not_write_itself_or_a_stream_left_out(string json, bool hidden)
    {
        Assert.Equal(hidden, VideoStripMetadata.HasHiddenInfo(MediaInfo.Parse(json)));
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
