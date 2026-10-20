using System.Collections.Concurrent;
using static Flevert.Media.Tests.TestFfmpeg;

namespace Flevert.Media.Tests;

/// <summary>
/// Video and audio made by ffmpeg itself from its test sources (lavfi <c>testsrc2</c>, <c>sine</c>) the first time a test
/// asks, once per test run, so no media file is kept in the repository (fixtures/README.md). Tests copy what they use
/// into their own folder (<see cref="CopyAsync"/>) and never change these.
/// </summary>
internal static class Fixtures
{
    private static readonly string Folder = CreateFolder();
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Made = new();

    /// <summary>10 s, 1920 × 1080 at 30 fps, H.264 + AAC stereo 48 kHz in MP4, with a title and a creation date.</summary>
    public static Task<string> Video1080pAsync() => Make("clip-1080p.mp4", path => RunRawAsync(
        "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=10", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=10",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-ac", "2",
        "-metadata", "title=Holiday", "-metadata", "creation_time=2026-07-14T09:30:00Z", "-f", "mp4", path));

    /// <summary>4 s, 640 × 360 at 30 fps, H.264 + AAC stereo in MP4, with a title: quick to encode again.</summary>
    public static Task<string> SmallClipAsync() => Make("small.mp4", path => RunRawAsync(
        "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=4", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", "-metadata", "title=Small", "-f", "mp4", path));

    /// <summary>
    /// As a phone records it upright: 4 s of 640 × 360 pixels stored sideways, turned 90° by the display matrix, in MOV,
    /// with the place it was filmed (Apple's key and the plain one) and a creation date.
    /// </summary>
    public static Task<string> PhoneMovAsync() => Make("phone.mov", async path =>
    {
        var plain = path + ".plain.mov";
        await RunRawAsync(
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=4", "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000:duration=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2",
            "-metadata", "location=+48.8577+002.2950/", "-metadata", "location-eng=+48.8577+002.2950/",
            "-metadata", "com.apple.quicktime.location.ISO6709=+48.8577+002.2950+035.000/", "-metadata", "creation_time=2026-07-14T09:30:00Z",
            "-movflags", "use_metadata_tags", "-f", "mov", plain);
        await RunRawAsync("-display_rotation", "90", "-i", plain, "-map", "0", "-c", "copy", "-map_metadata", "0", "-movflags", "use_metadata_tags", "-f", "mov", path);
        File.Delete(plain);
    });

    /// <summary>4 s of 1280 × 720 HEVC 10-bit tagged as HDR10 (BT.2020, PQ), as an iPhone's HDR video is.</summary>
    public static Task<string> HdrMovAsync() => Make("hdr.mov", path => RunRawAsync(
        "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=4", "-f", "lavfi", "-i", "sine=frequency=550:sample_rate=48000:duration=4",
        "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=error",
        "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", "-tag:v", "hvc1",
        "-c:a", "aac", "-ac", "2", "-f", "mov", path));

    /// <summary>6 s of 320 × 240 VP9 in MKV with two audio streams: English Opus, then Vietnamese Vorbis marked default.</summary>
    public static Task<string> TwoAudioMkvAsync() => Make("two-audio.mkv", path => RunRawAsync(
        "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=6", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6",
        "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100:duration=6",
        "-map", "0", "-map", "1", "-map", "2", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "200k",
        "-c:a:0", "libopus", "-c:a:1", "libvorbis", "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=vie",
        "-disposition:a:0", "0", "-disposition:a:1", "default", "-f", "matroska", path));

    /// <summary>10 s of a 440 Hz tone, stereo 44.1 kHz 16-bit WAV, tagged with a title and an artist.</summary>
    public static Task<string> WavAsync() => Make("tone.wav", path => RunRawAsync(
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=10", "-ac", "2", "-c:a", "pcm_s16le",
        "-metadata", "title=Tone", "-metadata", "artist=Flevert tests", "-f", "wav", path));

    /// <summary>10 s of AAC in M4A, the way phones and iTunes write it.</summary>
    public static Task<string> M4aAsync() => Make("tone.m4a", async path => await RunRawAsync(
        "-i", await WavAsync(), "-c:a", "aac", "-b:a", "128k", "-f", "ipod", path));

    /// <summary>A copy of <paramref name="fixture"/> named <paramref name="name"/> in <paramref name="dir"/>.</summary>
    public static async Task<string> CopyAsync(Task<string> fixture, TempDir dir, string name)
    {
        var path = dir.Combine(name);
        File.Copy(await fixture, path);
        return path;
    }

    private static Task<string> Make(string name, Func<string, Task> make) => Made.GetOrAdd(name, _ => new Lazy<Task<string>>(async () =>
    {
        var path = Path.Combine(Folder, name);
        await make(path);
        return path;
    })).Value;

    private static string CreateFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "flevert-tests", $"media-fixtures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        return folder;
    }
}
