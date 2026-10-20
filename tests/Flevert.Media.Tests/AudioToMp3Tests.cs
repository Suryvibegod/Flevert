using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Flevert.Core;

namespace Flevert.Media.Tests;

/// <summary>The first slice of v1.3: Convert &gt; MP3, from the probe to the output beside the input.</summary>
public sealed class AudioToMp3Tests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op() => MediaOperations.Create(TestFfmpeg.Instance).OfType<IOperation>().Single(op => op.Id == "audio.to-mp3");

    [Fact]
    public void It_is_named_and_placed_as_in_the_spec_and_never_offered_for_an_mp3()
    {
        var op = Op();

        Assert.Equal("Convert to/MP3", op.MenuPath);
        Assert.Equal([".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".aif"], op.Extensions);
        Assert.True(op.MaxParallel > 1);
        Assert.Contains(MediaOperations.All, all => all.Id == "audio.to-mp3");
    }

    [Fact]
    public async Task A_wav_becomes_an_mp3_of_the_same_length_beside_it_with_its_tags_and_the_wav_unchanged()
    {
        var input = await Fixtures.CopyAsync(Fixtures.WavAsync(), _dir, "Bài hát.wav");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(input));

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Bài hát.mp3"), output);
        var probe = await Probe(output);
        Assert.Equal("mp3", probe.Format);
        Assert.Equal(["mp3"], probe.Codecs);
        Assert.Equal(10, probe.Duration, 1);
        Assert.Equal("Tone", probe.Tags["title"]);
        Assert.Equal("Flevert tests", probe.Tags["artist"]);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        Assert.Equal(["Bài hát.mp3", "Bài hát.wav"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_existing_mp3_of_that_name_is_kept_and_the_output_takes_the_next_free_name()
    {
        var input = await Fixtures.CopyAsync(Fixtures.WavAsync(), _dir, "tone.wav");
        var taken = _dir.File("tone.mp3", "mine");

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("tone (1).mp3"), output);
        Assert.Equal("mine", await File.ReadAllTextAsync(taken));
    }

    [Fact]
    public async Task A_file_deep_in_folders_past_260_characters_converts()
    {
        // ffmpeg turns a long Windows path into the \\?\ form itself (libavutil's win32_open); the temporary output beside
        // the input is longer still.
        var folder = Path.Combine(_dir.Path, new string('a', 100), new string('b', 100), new string('c', 60));
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "tone.wav");
        File.Copy(await Fixtures.WavAsync(), input);
        Assert.True(input.Length > 260, $"{input.Length} characters");

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(Path.Combine(folder, "tone.mp3"), output);
        Assert.Equal("mp3", (await TestFfmpeg.Instance.ProbeAsync(output, CancellationToken.None)).MainAudio!.Codec);
    }

    [Fact]
    public async Task Reports_how_much_of_the_file_is_done_as_a_rising_percentage_that_ends_at_100()
    {
        var input = await Fixtures.CopyAsync(Fixtures.WavAsync(), _dir, "tone.wav");
        var reports = new List<FileProgress>();

        await Op().RunAsync(input, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.All(reports, report => Assert.True(report.IsPercent));
        Assert.Equal(100, reports[^1].Position);
        Assert.Equal(reports.Select(r => r.Position).Order(), reports.Select(r => r.Position));
        Assert.Equal(reports.Count, reports.Select(r => r.Position).Distinct().Count());
    }

    [Fact]
    public async Task Gps_in_the_input_never_reaches_the_mp3()
    {
        var input = _dir.Combine("voice memo.m4a");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.WavAsync(), "-c:a", "aac", "-metadata", "title=Memo", "-metadata", "location=+10.7769+106.7009/",
            "-metadata", "location-eng=+10.7769+106.7009/", "-f", "ipod", input);
        Assert.Contains("+10.7769", await TestFfmpeg.ProbeRawAsync("-show_entries", "format_tags", "-of", "json", input), StringComparison.Ordinal);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var tags = await TestFfmpeg.ProbeRawAsync("-show_entries", "format_tags:stream_tags", "-of", "json", output);
        Assert.Contains("Memo", tags, StringComparison.Ordinal);
        Assert.DoesNotContain("10.7769", tags, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_without_sound_is_refused_and_nothing_is_written()
    {
        var input = _dir.Combine("silent.m4a");
        await TestFfmpeg.RunRawAsync("-i", await Fixtures.Video1080pAsync(), "-an", "-c", "copy", "-t", "1", "-f", "mp4", input);

        var e = await Assert.ThrowsAsync<MediaException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(MediaException.NoSoundMessage, e.Message);
        Assert.Equal(["silent.m4a"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cancelling_stops_ffmpeg_at_once_and_leaves_no_partial_file()
    {
        // 20 minutes of 8 kHz mono: written in an instant, a few seconds of encoding.
        var input = _dir.Combine("long.wav");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "sine=frequency=300:sample_rate=8000:duration=1200", "-c:a", "pcm_u8", "-f", "wav", input);
        using var cancel = new CancellationTokenSource();
        var cancelledAt = new TaskCompletionSource<Stopwatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SyncProgress(report =>
        {
            if (report.Position >= 1 && !cancel.IsCancellationRequested)
            {
                cancelledAt.TrySetResult(Stopwatch.StartNew());
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op().RunAsync(input, progress, cancel.Token));

        var sinceCancel = (await cancelledAt.Task).Elapsed;
        Assert.True(sinceCancel < TimeSpan.FromSeconds(2), $"returned {sinceCancel} after the cancel");
        Assert.Equal(["long.wav"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task An_output_that_reaches_its_size_limit_is_an_error_not_a_short_file()
    {
        var input = await Fixtures.CopyAsync(Fixtures.WavAsync(), _dir, "tone.wav");
        var output = _dir.Combine("out.partial");

        var e = await Assert.ThrowsAsync<MediaException>(() => TestFfmpeg.Instance.EncodeAsync(
            input, ["-c:a", "libmp3lame", "-q:a", "2"], output, "mp3", TimeSpan.FromSeconds(10), 20_000, null, CancellationToken.None));

        Assert.Equal(MediaException.TooBigOutputMessage, e.Message);
    }

    [Fact]
    public void Too_little_disk_space_for_the_estimate_is_an_error_before_anything_runs()
    {
        var e = Assert.Throws<MediaException>(() => MediaJob.RequireSpace(_dir.Combine("out.mp3"), long.MaxValue / 4));

        Assert.StartsWith("Not enough free disk space", e.Message, StringComparison.Ordinal);
        MediaJob.RequireSpace(_dir.Combine("out.mp3"), 1_000);
    }

    [Fact]
    public async Task Ffmpeg_gets_the_input_only_as_a_local_file_url_behind_both_whitelists_and_never_overwrites()
    {
        var calls = new List<IReadOnlyList<string>>();
        var recording = new Ffmpeg(TestFfmpeg.Instance.FfmpegPath, TestFfmpeg.Instance.FfprobePath, (tool, arguments, timeout, line, ct) =>
        {
            calls.Add(arguments);
            return TestFfmpeg.Instance.Run(tool, arguments, timeout, line, ct);
        });
        var input = await Fixtures.CopyAsync(Fixtures.WavAsync(), _dir, "-y.wav");

        var output = await MediaOperations.Create(recording).OfType<IOperation>().Single(op => op.Id == "audio.to-mp3").RunAsync(input, CancellationToken.None);

        Assert.Equal(2, calls.Count); // ffprobe, then ffmpeg
        foreach (var arguments in calls)
        {
            var at = arguments.ToList().IndexOf("-i");
            Assert.Equal(["-protocol_whitelist", "file", "-format_whitelist", Ffmpeg.Demuxers], arguments.Skip(at - 4).Take(4));
            Assert.Equal("file:" + input, arguments[at + 1]);
            Assert.Single(arguments, argument => argument.Contains(input, StringComparison.Ordinal));
        }

        var encode = calls[1];
        Assert.Contains("-nostdin", encode);
        Assert.Contains("-n", encode);
        Assert.DoesNotContain("-y", encode);
        Assert.StartsWith("file:" + _dir.Path, encode[^1], StringComparison.Ordinal);
        Assert.EndsWith(".partial", encode[^1], StringComparison.Ordinal);
        Assert.Equal(["-f", "mp3"], encode.Skip(encode.Count - 3).Take(2));
        Assert.Equal(_dir.Combine("-y.mp3"), output);
    }

    private static async Task<(string Format, string[] Codecs, double Duration, Dictionary<string, string> Tags)> Probe(string path)
    {
        using var json = JsonDocument.Parse(await TestFfmpeg.ProbeRawAsync("-show_format", "-show_streams", "-of", "json", path));
        var format = json.RootElement.GetProperty("format");
        var tags = format.TryGetProperty("tags", out var t)
            ? t.EnumerateObject().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.GetString()!)
            : [];
        return (format.GetProperty("format_name").GetString()!,
            [.. json.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_name").GetString()!)],
            double.Parse(format.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), tags);
    }
}
