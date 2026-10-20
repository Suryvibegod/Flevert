using System.Text.Json;
using Flevert.Core;

namespace Flevert.Media.Tests;

/// <summary>Convert &gt; GIF (docs/specs/media.md): 480 pixels at most, 12 frames a second, the first 30 seconds.</summary>
public sealed class VideoToGifTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op() => MediaOperations.Create(TestFfmpeg.Instance).OfType<IOperation>().Single(op => op.Id == "video.to-gif");

    [Fact]
    public void Gif_is_the_last_of_convert_for_every_video_type()
    {
        var convert = MediaOperations.Create(TestFfmpeg.Instance).Where(op => op.Id.StartsWith("video.to-", StringComparison.Ordinal)).ToList();

        Assert.Equal("Convert/GIF", convert[^1].MenuPath);
        Assert.Equal(MediaJob.VideoExtensions, Op().Extensions);
        Assert.DoesNotContain(".gif", MediaOperations.Create(TestFfmpeg.Instance).SelectMany(op => op.Extensions));
    }

    [Fact]
    public async Task A_clip_becomes_a_looping_gif_480_pixels_wide_at_12_frames_a_second()
    {
        var input = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "meme.mp4");

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("meme.gif"), output);
        var gif = await Probe(output);
        Assert.Equal(("gif", 480, 270, "12/1"), (gif.Codec, gif.Width, gif.Height, gif.Rate));
        Assert.Equal(4, gif.Duration, 1);
    }

    [Fact]
    public async Task A_phone_video_becomes_an_upright_gif_480_pixels_tall_and_a_small_one_keeps_its_size()
    {
        var phone = await Fixtures.CopyAsync(Fixtures.PhoneMovAsync(), _dir, "phone.mov");
        var small = _dir.Combine("small.mp4");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.SmallClipAsync(), "-vf", "scale=320:180", "-c:v", "libx264", "-preset", "ultrafast", "-an", "-f", "mp4", small);

        var upright = await Probe(await Op().RunAsync(phone, CancellationToken.None));
        var kept = await Probe(await Op().RunAsync(small, CancellationToken.None));

        Assert.Equal((270, 480), (upright.Width, upright.Height));
        Assert.Equal((320, 180), (kept.Width, kept.Height));
    }

    [Fact]
    public async Task Only_the_first_30_seconds_of_a_longer_video_are_converted()
    {
        var input = await LongVideo();

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(30, (await Probe(output)).Duration, 1);
    }

    [WindowsFact]
    public async Task The_notification_says_when_only_the_first_30_seconds_were_converted()
    {
        var input = await LongVideo();
        var short_ = await Fixtures.CopyAsync(Fixtures.SmallClipAsync(), _dir, "short.mp4");
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [input, short_], null, CancellationToken.None);

        Assert.All(outcome.Results, result => Assert.Null(result.Error));
        Assert.Equal([VideoToGif.FirstSecondsNote, null], outcome.Results.Select(r => r.Note));
    }

    private async Task<string> LongVideo()
    {
        var input = _dir.Combine("long.mp4");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=12:duration=45", "-c:v", "libx264", "-preset", "ultrafast", "-f", "mp4", input);
        return input;
    }

    private static async Task<(string Codec, int Width, int Height, string Rate, double Duration)> Probe(string path)
    {
        using var json = JsonDocument.Parse(await TestFfmpeg.ProbeRawAsync("-show_format", "-show_streams", "-of", "json", path));
        var stream = json.RootElement.GetProperty("streams")[0];
        return (stream.GetProperty("codec_name").GetString()!, stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32(),
            stream.GetProperty("r_frame_rate").GetString()!,
            double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }
}
