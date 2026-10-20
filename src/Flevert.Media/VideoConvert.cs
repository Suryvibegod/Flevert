using Flevert.Core;

namespace Flevert.Media;

/// <summary>A video file type Flevert writes with Convert (docs/specs/media.md, table of operations).</summary>
/// <param name="Slowness">How much slower than x264 its encoder is, for the timeout (<see cref="VideoEncoding.Slowness"/>).</param>
internal sealed record VideoFormat(string Label, string Extension, string Muxer, string[] Codecs, double Slowness = 1)
{
    // +faststart moves the index to the front, so a player or a browser starts before the whole file has arrived.
    private static readonly string[] H264Aac = [.. VideoEncoding.H264(20), .. VideoEncoding.Aac(192)];

    public static VideoFormat Mp4 { get; } = new("MP4", ".mp4", "mp4", [.. H264Aac, "-movflags", "+faststart"]);

    public static VideoFormat Mov { get; } = new("MOV", ".mov", "mov", [.. H264Aac, "-movflags", "+faststart"]);

    /// <summary>
    /// VP9 at constant quality 32 and Opus 128 kbit/s. <c>-cpu-used 4</c>: 1.8 times real time for 1080p on 4 cores,
    /// 27 % faster than 2 for a file 3 % larger (measured 2026-10-20); still about twice as slow as x264, so its passes
    /// may take twice as long.
    /// </summary>
    public static VideoFormat Webm { get; } = new("WEBM", ".webm", "webm",
        ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k"],
        Slowness: 2);

    // Not MKV: few people need it as an output (docs/specs/media.md); it stays a source.
    public static IReadOnlyList<VideoFormat> All { get; } = [Mp4, Mov, Webm];
}

/// <summary>
/// <c>Convert to &gt; MP4</c> (and MOV, WEBM): writes the video as <c>&lt;name&gt;.&lt;ext&gt;</c> beside it, at its own
/// size and frame rate, upright, in SDR, with its first sound stream and the file's own tags but none that say where it
/// was filmed. Not offered for a file already of that type.
/// </summary>
public sealed class VideoConvert : IOperation
{
    private readonly VideoFormat _format;
    private readonly Ffmpeg _ffmpeg;

    internal VideoConvert(VideoFormat format, Ffmpeg ffmpeg)
    {
        _format = format;
        _ffmpeg = ffmpeg;
        Id = $"video.to-{format.Label.ToLowerInvariant()}";
        MenuPath = $"Convert to/{format.Label}";
        Extensions = [.. MediaJob.VideoExtensions.Where(extension => extension != format.Extension)];
    }

    public string Id { get; }

    public string MenuPath { get; }

    public IReadOnlyCollection<string> Extensions { get; }

    // The encoder already uses every core.
    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var info = await _ffmpeg.ProbeAsync(inputPath, ct).ConfigureAwait(false);
        var video = info.Video ?? throw new MediaException(MediaException.NoVideoMessage);
        var audio = VideoEncoding.FirstAudio(info);
        var size = VideoEncoding.OutputSize(video);
        var estimate = VideoEncoding.EstimateBytes(info, size, 192, audio.Count);
        var desired = MediaJob.Beside(inputPath, _format.Extension);
        MediaJob.RequireSpace(desired, estimate);
        string[] options =
        [
            .. VideoEncoding.Maps(info, audio), .. MediaJob.MetadataOptions(info, audio),
            .. VideoEncoding.Filters(video, size), .. VideoEncoding.FrameTimes, .. _format.Codecs,
        ];
        return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _ffmpeg.EncodeAsync(
            inputPath, options, temp, _format.Muxer, info.Duration, MediaJob.MaxOutputBytes(inputPath, estimate, desired), MediaJob.Percent(progress), token,
            VideoEncoding.Slowness(video, _format.Slowness), VideoEncoding.DecoderThreads, boundTime: true), ct).ConfigureAwait(false);
    }
}
