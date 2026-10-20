using System.Diagnostics;

namespace Flevert.Media.Tests;

/// <summary>Probing real files with the ffprobe Flevert runs (TestFfmpeg).</summary>
public sealed class FfprobeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Reads_a_1080p_clip_as_ffmpeg_wrote_it()
    {
        var info = await TestFfmpeg.Instance.ProbeAsync(await Fixtures.CopyAsync(Fixtures.Video1080pAsync(), _dir, "clip.mp4"), CancellationToken.None);

        Assert.Equal(10, info.Duration.TotalSeconds, 1);
        Assert.Equal(("h264", 1920, 1080, 0, false), (info.Video!.Codec, info.Video.Width, info.Video.Height, info.Video.Rotation, info.Video.IsHdr));
        Assert.Equal(30, info.Video.FrameRate, 1);
        Assert.Equal(("aac", 2, 48000), (info.MainAudio!.Codec, info.MainAudio.Channels, info.MainAudio.SampleRate));
        Assert.Equal("Holiday", info.Tags["title"]);
    }

    [Fact]
    public async Task Reads_how_far_a_phone_video_turns_and_the_hdr_of_an_iphone_one()
    {
        var phone = await TestFfmpeg.Instance.ProbeAsync(await Fixtures.CopyAsync(Fixtures.PhoneMovAsync(), _dir, "phone.mov"), CancellationToken.None);
        var hdr = await TestFfmpeg.Instance.ProbeAsync(await Fixtures.CopyAsync(Fixtures.HdrMovAsync(), _dir, "hdr.mov"), CancellationToken.None);

        Assert.Equal((360, 640), (phone.Video!.DisplayWidth, phone.Video.DisplayHeight));
        Assert.True(hdr.Video!.IsHdr);
        Assert.Equal("hevc", hdr.Video.Codec);
    }

    [Fact]
    public async Task Lists_every_audio_stream_and_picks_the_first_one()
    {
        var info = await TestFfmpeg.Instance.ProbeAsync(await Fixtures.CopyAsync(Fixtures.TwoAudioMkvAsync(), _dir, "two.mkv"), CancellationToken.None);

        Assert.Equal(["opus", "vorbis"], info.Audio.Select(a => a.Codec));
        Assert.Equal(("opus", "eng"), (info.MainAudio!.Codec, info.MainAudio.Language));
    }

    [Theory]
    [InlineData("hls.mp4", "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nhttp://127.0.0.1:9/a.ts\n#EXT-X-ENDLIST\n")]
    [InlineData("list.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nhttp://127.0.0.1:9/a.ts\n#EXT-X-ENDLIST\n")]
    [InlineData("concat.mp4", "ffconcat version 1.0\nfile other.mp4\n")]
    [InlineData("script.avi", "#!/bin/sh\necho not a video\n")]
    [InlineData("empty.mp4", "")]
    [InlineData("noise.mkv", "\u0001\u0002\u0003 definitely not matroska")]
    public async Task Refuses_what_is_not_video_or_audio_in_a_demuxer_flevert_allows(string name, string content)
    {
        var path = _dir.File(name, content);

        var stopwatch = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<MediaException>(() => TestFfmpeg.Instance.ProbeAsync(path, CancellationToken.None));

        Assert.Equal(MediaException.UnreadableMessage, e.Message);
        Assert.IsType<FfmpegException>(e.InnerException);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"{stopwatch.Elapsed}");
    }

    [Fact]
    public async Task A_concat_list_disguised_as_a_video_is_refused_while_the_file_it_names_is_a_real_video()
    {
        // Without the demuxer whitelist ffprobe would follow the list and read the clip beside it.
        await Fixtures.CopyAsync(Fixtures.Video1080pAsync(), _dir, "other.mp4");
        var list = _dir.File("holiday.mp4", "ffconcat version 1.0\nfile other.mp4\n");

        var e = await Assert.ThrowsAsync<MediaException>(() => TestFfmpeg.Instance.ProbeAsync(list, CancellationToken.None));

        Assert.Contains("not on whitelist", e.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_relative_path_is_refused_before_ffprobe_runs()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => TestFfmpeg.Instance.ProbeAsync("clip.mp4", CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_ffprobe_is_named_with_how_to_get_it_back()
    {
        var missing = new Ffmpeg(_dir.Path);

        var e = await Assert.ThrowsAsync<FileNotFoundException>(() => missing.ProbeAsync(_dir.File("a.mp4"), CancellationToken.None));

        Assert.Contains("ffprobe.exe is missing", e.Message, StringComparison.Ordinal);
    }
}
