namespace Flevert.Media.Tests;

/// <summary>Reading ffprobe's JSON, with the shapes ffprobe 6 to 8 write, and the files Flevert refuses up front.</summary>
public sealed class MediaInfoTests
{
    private const string Clip = """
        {
          "streams": [
            { "index": 0, "codec_name": "hevc", "codec_type": "video", "width": 1920, "height": 1080, "avg_frame_rate": "30000/1001",
              "color_transfer": "arib-std-b67", "disposition": { "default": 1, "attached_pic": 0 },
              "side_data_list": [ { "side_data_type": "Display Matrix", "displaymatrix": "...", "rotation": -90 } ] },
            { "index": 1, "codec_name": "aac", "codec_type": "audio", "sample_rate": "44100", "channels": 2, "bit_rate": "192000",
              "disposition": { "default": 1 }, "tags": { "language": "und" } },
            { "index": 2, "codec_name": "none", "codec_type": "data" }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "12.345000", "bit_rate": "9000000",
            "tags": { "creation_time": "2026-07-14T09:30:00.000000Z", "Title": "Holiday",
              "com.apple.quicktime.location.ISO6709": "+48.8577+002.2950+035.000/" } }
        }
        """;

    [Fact]
    public void Reads_the_duration_the_streams_and_the_tags_of_a_phone_video()
    {
        var info = MediaInfo.Parse(Clip);

        Assert.Equal(12.345, info.Duration.TotalSeconds, 3);
        Assert.Equal(9_000_000, info.BitRate);
        var video = Assert.IsType<VideoTrack>(info.Video);
        Assert.Equal((0, "hevc", 1920, 1080), (video.Index, video.Codec, video.Width, video.Height));
        Assert.Equal(29.97, video.FrameRate, 2);
        Assert.True(video.IsHdr);
        var audio = Assert.Single(info.Audio);
        Assert.Equal((1, "aac", 2, 44100, 192_000L), (audio.Index, audio.Codec, audio.Channels, audio.SampleRate, audio.BitRate!.Value));
        Assert.Same(audio, info.MainAudio);
        // Keys in lower case, values as the file has them; GPS is read too, and dropped by the operations.
        Assert.Equal("Holiday", info.Tags["title"]);
        Assert.Equal("2026-07-14T09:30:00.000000Z", info.Tags["creation_time"]);
    }

    [Fact]
    public void A_display_matrix_of_minus_90_degrees_turns_the_picture_upright_with_the_sides_swapped()
    {
        var video = MediaInfo.Parse(Clip).Video!;

        Assert.Equal(270, video.Rotation);
        Assert.Equal((1080, 1920), (video.DisplayWidth, video.DisplayHeight));
    }

    [Fact]
    public void The_old_rotate_tag_counts_when_there_is_no_display_matrix()
    {
        var info = MediaInfo.Parse(Json("""{ "index": 0, "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480, "tags": { "rotate": "90" } }"""));

        Assert.Equal(90, info.Video!.Rotation);
        Assert.Equal((480, 640), (info.Video.DisplayWidth, info.Video.DisplayHeight));
    }

    [Theory]
    [InlineData("smpte2084", true)]
    [InlineData("arib-std-b67", true)]
    [InlineData("bt709", false)]
    [InlineData(null, false)]
    public void Pq_and_hlg_are_hdr(string? transfer, bool hdr)
    {
        var colour = transfer is null ? "" : $"\"color_transfer\": \"{transfer}\",";
        var info = MediaInfo.Parse(Json($$"""{ "index": 0, "codec_type": "video", {{colour}} "codec_name": "h264", "width": 64, "height": 64 }"""));

        Assert.Equal(hdr, info.Video!.IsHdr);
    }

    [Fact]
    public void Cover_art_is_not_the_video_of_a_song()
    {
        var info = MediaInfo.Parse(Json(
            """{ "index": 0, "codec_type": "audio", "codec_name": "mp3", "channels": 2, "sample_rate": "44100" }""",
            """{ "index": 1, "codec_type": "video", "codec_name": "mjpeg", "width": 600, "height": 600, "disposition": { "attached_pic": 1 } }"""));

        Assert.Null(info.Video);
        Assert.Equal("mp3", Assert.Single(info.Audio).Codec);
    }

    [Fact]
    public void The_first_audio_stream_is_the_main_one_even_when_another_is_marked_default()
    {
        // docs/specs/media.md: the first video stream and the first audio stream.
        var marked = MediaInfo.Parse(Json(
            """{ "index": 0, "codec_type": "audio", "codec_name": "opus", "disposition": { "default": 0 } }""",
            """{ "index": 1, "codec_type": "audio", "codec_name": "vorbis", "disposition": { "default": 1 } }"""));

        Assert.Equal(0, marked.MainAudio!.Index);
    }

    [Fact]
    public void Streams_that_are_neither_the_video_nor_sound_are_counted()
    {
        var info = MediaInfo.Parse(Json(
            """{ "index": 0, "codec_type": "video", "codec_name": "h264", "width": 64, "height": 64, "tags": { "LOCATION": "+1+2/" } }""",
            """{ "index": 1, "codec_type": "video", "codec_name": "mjpeg", "width": 64, "height": 64, "disposition": { "attached_pic": 1 } }""",
            """{ "index": 2, "codec_type": "data", "codec_name": "none", "tags": { "handler_name": "Core Media Metadata" } }""",
            """{ "index": 3, "codec_type": "subtitle", "codec_name": "mov_text" }""",
            """{ "index": 4, "codec_type": "attachment", "codec_name": "ttf" }"""));

        Assert.Equal(4, info.OtherStreams);
        Assert.Equal("+1+2/", info.Video!.Tags["location"]);
    }

    [Fact]
    public void Without_a_duration_in_the_format_the_longest_stream_counts()
    {
        var info = MediaInfo.Parse("""
            { "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "flac", "duration": "61.5" } ], "format": { } }
            """);

        Assert.Equal(61.5, info.Duration.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("""{ "streams": [], "format": { "duration": "10" } }""", MediaException.NoStreamMessage)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "data" } ], "format": { "duration": "10" } }""", MediaException.NoStreamMessage)]
    [InlineData("""{ "format": { "duration": "10" } }""", MediaException.NoStreamMessage)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "audio" } ], "format": { "duration": "0.000000" } }""", MediaException.UnknownDurationMessage)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "audio" } ], "format": { "duration": "N/A" } }""", MediaException.UnknownDurationMessage)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "audio" } ], "format": { } }""", MediaException.UnknownDurationMessage)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "audio" } ], "format": { "duration": "-5" } }""", MediaException.UnknownDurationMessage)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "audio" } ], "format": { "duration": "356400" } }""", MediaException.TooLongMessage)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "video", "width": 15360, "height": 8640 } ], "format": { "duration": "1" } }""", MediaException.TooLargeVideoMessage)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "video", "width": 100000, "height": 2 } ], "format": { "duration": "1" } }""", MediaException.TooLargeVideoMessage)]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "video", "width": 0, "height": 0 } ], "format": { "duration": "1" } }""", MediaException.UnreadableMessage)]
    [InlineData("not json", MediaException.UnreadableMessage)]
    [InlineData("[1, 2]", MediaException.UnreadableMessage)]
    public void Refuses_what_flevert_does_not_convert_with_the_reason(string json, string message)
    {
        var e = Assert.Throws<MediaException>(() => MediaInfo.Parse(json));

        Assert.Equal(message, e.Message);
    }

    [Fact]
    public void Twelve_hours_and_8k_are_still_accepted()
    {
        var info = MediaInfo.Parse("""
            { "streams": [ { "index": 0, "codec_type": "video", "width": 7680, "height": 4320 } ], "format": { "duration": "43200" } }
            """);

        Assert.Equal(TimeSpan.FromHours(12), info.Duration);
    }

    private static string Json(params string[] streams) => $$"""{ "streams": [ {{string.Join(", ", streams)}} ], "format": { "duration": "5" } }""";
}
