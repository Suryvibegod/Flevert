using System.Text.Json;
using Flevert.Core;

namespace Flevert.Media.Tests;

/// <summary>Every audio conversion of docs/specs/media.md, and Extract audio from a video (success criteria 3 and 4).</summary>
public sealed class AudioConvertTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string id) => MediaOperations.Create(TestFfmpeg.Instance).OfType<IOperation>().Single(op => op.Id == id);

    public static TheoryData<string, string, string, string> Targets => new()
    {
        // id, output extension, ffprobe's format name, codec
        { "audio.to-mp3", ".mp3", "mp3", "mp3" },
        { "audio.to-m4a", ".m4a", "mov,mp4,m4a,3gp,3g2,mj2", "aac" },
        { "audio.to-wav", ".wav", "wav", "pcm_s16le" },
        { "audio.to-flac", ".flac", "flac", "flac" },
        { "audio.to-ogg", ".ogg", "ogg", "vorbis" },
        { "audio.to-opus", ".opus", "ogg", "opus" },
    };

    [Fact]
    public void The_convert_menu_of_an_audio_file_offers_every_other_type_in_the_spec_order()
    {
        var convert = MediaOperations.Create(TestFfmpeg.Instance).Where(op => op.Id.StartsWith("audio.", StringComparison.Ordinal)).ToList();

        Assert.Equal(
            ["Convert to/MP3", "Convert to/M4A", "Convert to/WAV", "Convert to/FLAC", "Convert to/OGG", "Convert to/Opus"],
            convert.Select(op => op.MenuPath));
        foreach (var extension in MediaJob.AudioExtensions)
        {
            var offered = convert.Where(op => op.Extensions.Contains(extension)).Select(op => op.Id[^4..].TrimStart('-')).ToList();
            Assert.DoesNotContain(extension.TrimStart('.'), offered);
            Assert.Equal(extension is ".mp3" or ".m4a" or ".wav" or ".flac" or ".ogg" or ".opus" ? 5 : 6, offered.Count);
        }
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task A_wav_converts_to_each_type_with_its_codec_and_length(string id, string extension, string format, string codec)
    {
        // A WAV is never converted to WAV: that one starts from the M4A.
        var input = extension == ".wav"
            ? await Fixtures.CopyAsync(Fixtures.M4aAsync(), _dir, "tone.m4a")
            : await Fixtures.CopyAsync(Fixtures.WavAsync(), _dir, "tone.wav");

        var output = await Op(id).RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("tone" + extension), output);
        var probe = await Probe(output);
        Assert.Equal((format, codec), (probe.Format, Assert.Single(probe.Codecs)));
        Assert.Equal(10, probe.Duration, 1);
        Assert.Equal("Tone", probe.Tags.GetValueOrDefault("title"));
    }

    [Theory]
    [InlineData(".mp3", "-c:a libmp3lame -f mp3")]
    [InlineData(".aac", "-c:a aac -f adts")]
    [InlineData(".m4a", "-c:a aac -f ipod")]
    [InlineData(".flac", "-c:a flac -f flac")]
    [InlineData(".ogg", "-c:a libvorbis -f ogg")]
    [InlineData(".opus", "-c:a libopus -f opus")]
    [InlineData(".wma", "-c:a wmav2 -f asf")]
    [InlineData(".aiff", "-c:a pcm_s16be -f aiff")]
    [InlineData(".aif", "-c:a pcm_s16be -f aiff")]
    public async Task Every_source_type_is_read_through_the_demuxers_flevert_allows(string extension, string make)
    {
        var input = _dir.Combine("source" + extension);
        await TestFfmpeg.RunRawAsync(["-i", await Fixtures.WavAsync(), .. make.Split(' '), input]);
        var id = extension == ".m4a" ? "audio.to-wav" : "audio.to-m4a";

        var output = await Op(id).RunAsync(input, CancellationToken.None);

        Assert.Equal(10, (await Probe(output)).Duration, 1);
    }

    [Theory]
    [InlineData("estimate.aac", "-c:a aac -f adts")]
    [InlineData("estimate.mp3", "-c:a libmp3lame -q:a 2 -write_xing 0 -f mp3")]
    public async Task A_file_whose_length_ffprobe_only_estimates_is_converted_whole(string name, string make)
    {
        // A loud start and a quiet rest: ffprobe guesses the length of a raw AAC, or of an MP3 without a VBR header, from
        // the first frames (5 minutes read as 30 s or 100 s). Nothing may cut it there (re-check review, RC-1).
        var input = _dir.Combine(name);
        await TestFfmpeg.RunRawAsync(["-f", "lavfi", "-i", "anoisesrc=d=20:a=0.8,apad=whole_dur=300", "-ar", "44100", "-ac", "2", .. make.Split(' '), input]);
        Assert.True((await TestFfmpeg.Instance.ProbeAsync(input, CancellationToken.None)).Duration < TimeSpan.FromSeconds(200));

        var output = await Op("audio.to-wav").RunAsync(input, CancellationToken.None);

        Assert.Equal(300, (await Probe(output)).Duration, 0);
    }

    [Fact]
    public async Task Aac_becomes_m4a_without_being_encoded_again()
    {
        var input = _dir.Combine("radio.aac");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.WavAsync(), "-c:a", "aac", "-b:a", "96k", "-f", "adts", input);

        var output = await Op("audio.to-m4a").RunAsync(input, CancellationToken.None);

        Assert.Equal(await AudioHash(input), await AudioHash(output));
    }

    [Fact]
    public async Task The_title_of_an_ogg_song_kept_on_its_stream_reaches_the_mp3_but_not_its_gps()
    {
        var input = _dir.Combine("song.ogg");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.WavAsync(), "-map_metadata", "-1", "-c:a", "libvorbis",
            "-metadata:s:a:0", "title=Bài ca", "-metadata:s:a:0", "location=+10.7769+106.7009/", "-f", "ogg", input);

        var output = await Op("audio.to-mp3").RunAsync(input, CancellationToken.None);

        var tags = (await Probe(output)).Tags;
        Assert.Equal("Bài ca", tags["title"]);
        Assert.DoesNotContain(tags.Values, value => value.Contains("10.7769", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Surround_sound_becomes_stereo_in_an_mp3()
    {
        var input = _dir.Combine("film.flac");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.WavAsync(), "-ac", "6", "-c:a", "flac", "-f", "flac", input);

        var output = await Op("audio.to-mp3").RunAsync(input, CancellationToken.None);

        Assert.Equal(2, (await TestFfmpeg.Instance.ProbeAsync(output, CancellationToken.None)).MainAudio!.Channels);
    }

    [Fact]
    public void Extract_audio_offers_mp3_and_m4a_for_every_video_type()
    {
        var extract = MediaOperations.Create(TestFfmpeg.Instance).Where(op => op.Id.StartsWith("video.extract-", StringComparison.Ordinal)).ToList();

        Assert.Equal(["Extract audio/MP3", "Extract audio/M4A"], extract.Select(op => op.MenuPath));
        Assert.All(extract, op => Assert.Equal(MediaJob.VideoExtensions, op.Extensions));
    }

    [Fact]
    public async Task Extracting_mp3_from_a_video_keeps_its_length()
    {
        var input = await Fixtures.CopyAsync(Fixtures.Video1080pAsync(), _dir, "clip.mp4");

        var output = await Op("video.extract-mp3").RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("clip.mp3"), output);
        var probe = await Probe(output);
        Assert.Equal(["mp3"], probe.Codecs);
        Assert.Equal(10, probe.Duration, 1);
        Assert.Equal("Holiday", probe.Tags["title"]);
    }

    [Fact]
    public async Task Extracting_m4a_from_a_video_with_aac_copies_its_sound_as_it_is()
    {
        var input = await Fixtures.CopyAsync(Fixtures.Video1080pAsync(), _dir, "clip.mp4");

        var output = await Op("video.extract-m4a").RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("clip.m4a"), output);
        Assert.Equal(["aac"], (await Probe(output)).Codecs);
        Assert.Equal(await AudioHash(input), await AudioHash(output));
    }

    [Fact]
    public async Task Extracting_from_a_video_with_several_sound_tracks_takes_the_first_one()
    {
        var input = await Fixtures.CopyAsync(Fixtures.TwoAudioMkvAsync(), _dir, "film.mkv");

        var output = await Op("video.extract-m4a").RunAsync(input, CancellationToken.None);

        // The first track is the 48 kHz Opus one (not the one marked default), encoded to AAC at its rate.
        var info = await TestFfmpeg.Instance.ProbeAsync(output, CancellationToken.None);
        Assert.Equal(("aac", 48000), (info.MainAudio!.Codec, info.MainAudio.SampleRate));
        Assert.Equal(6, info.Duration.TotalSeconds, 1);
        Assert.Null(info.Video);
    }

    [Fact]
    public async Task A_video_without_sound_has_nothing_to_extract()
    {
        var input = _dir.Combine("silent.mp4");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.Video1080pAsync(), "-an", "-c", "copy", "-t", "1", "-f", "mp4", input);

        var e = await Assert.ThrowsAsync<MediaException>(() => Op("video.extract-mp3").RunAsync(input, CancellationToken.None));

        Assert.Equal(MediaException.NoSoundToExtractMessage, e.Message);
    }

    // The MD5 of the decoded sound: AAC encoded again never decodes to the same samples, a copied stream always does
    // (the packets themselves differ between ADTS, which carries a header per frame, and MP4, which does not).
    private static async Task<string> AudioHash(string path)
    {
        var output = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".md5");
        await TestFfmpeg.RunRawAsync("-i", path, "-map", "0:a:0", "-f", "md5", output);
        try
        {
            return (await File.ReadAllTextAsync(output)).Trim();
        }
        finally
        {
            File.Delete(output);
        }
    }

    internal static async Task<(string Format, string[] Codecs, double Duration, Dictionary<string, string> Tags)> Probe(string path)
    {
        using var json = JsonDocument.Parse(await TestFfmpeg.ProbeRawAsync("-show_format", "-show_streams", "-of", "json", path));
        var format = json.RootElement.GetProperty("format");
        // The file's tags, then those of its streams (Ogg and Opus keep them there).
        var tags = new Dictionary<string, string>();
        foreach (var holder in json.RootElement.GetProperty("streams").EnumerateArray().Prepend(format))
        {
            if (holder.TryGetProperty("tags", out var t))
            {
                foreach (var p in t.EnumerateObject())
                    tags.TryAdd(p.Name.ToLowerInvariant(), p.Value.GetString()!);
            }
        }

        return (format.GetProperty("format_name").GetString()!,
            [.. json.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_name").GetString()!)],
            double.Parse(format.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), tags);
    }
}
