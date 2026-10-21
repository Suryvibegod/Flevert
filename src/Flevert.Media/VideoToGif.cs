using System.Globalization;
using Flevert.Core;

namespace Flevert.Media;

/// <summary>
/// <c>Convert to &gt; GIF</c>: writes <c>&lt;name&gt;.gif</c> beside the video, at most 480 pixels on its long side and 12
/// frames a second, with a palette made for this video (docs/specs/media.md). A GIF of a long video would be huge, so
/// only the first 30 seconds are converted, and the notification says so.
/// </summary>
/// <remarks>
/// One ffmpeg run in two steps: <c>palettegen</c> reads every frame to choose the 256 colours, then <c>paletteuse</c> maps
/// the same frames, held meanwhile, onto them. At the limits that holds 30 s × 12 fps of 480 × 480 pixels, about
/// 330 MB, for the moment between the two; a palette file between two runs would need a second input Flevert must trust.
/// </remarks>
public sealed class VideoToGif : IOperation
{
    internal const int MaxSide = 480;
    internal const int FramesPerSecond = 12;
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);
    internal const string FirstSecondsNote = "Only the first 30 seconds were converted to GIF.";

    // Bayer dithering keeps the frame-to-frame noise, and with it the file, small; only changed rectangles are redrawn.
    private const string Palette =
        "split[frames][copy];[frames]palettegen=stats_mode=diff[palette];[copy][palette]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";

    private readonly Ffmpeg _ffmpeg;

    internal VideoToGif(Ffmpeg ffmpeg) => _ffmpeg = ffmpeg;

    public string Id => "video.to-gif";

    public string MenuPath => "Convert to/GIF";

    public IReadOnlyCollection<string> Extensions => MediaJob.VideoExtensions;

    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var info = await _ffmpeg.ProbeAsync(inputPath, ct).ConfigureAwait(false);
        var video = info.Video ?? throw new MediaException(MediaException.NoVideoMessage);
        var cut = info.Duration > MaxDuration;
        var duration = cut ? MaxDuration : info.Duration;
        var size = VideoEncoding.OutputSize(video, maxLongSide: MaxSide);
        // Half a byte per pixel and frame is more than a GIF of camera video takes.
        var estimate = (long)(size.Width * (double)size.Height * FramesPerSecond * duration.TotalSeconds / 2);
        var desired = MediaJob.Beside(inputPath, ".gif");
        MediaJob.RequireSpace(desired, estimate);
        // Frames dropped and the picture made small before the tone map, which works in floating point (code review,
        // 2026-10-21: 4K at 60 tone mapped whole was 70 times the work).
        var filters = new List<string>();
        if (video.IsInterlaced)
            filters.Add(VideoEncoding.Deinterlace);
        filters.Add(string.Create(CultureInfo.InvariantCulture, $"fps={FramesPerSecond}"));
        if (size != (video.DisplayWidth, video.DisplayHeight))
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"scale={size.Width}:{size.Height}:flags=lanczos"));
        if (video.IsHdr)
            filters.Add(VideoEncoding.ToneMap);
        filters.Add(Palette);
        string[] options =
        [
            "-map", $"0:{video.Index}", "-map_metadata", "-1", "-map_chapters", "-1",
            .. cut ? (string[])["-t", MaxDuration.TotalSeconds.ToString(CultureInfo.InvariantCulture)] : [],
            "-vf", string.Join(',', filters), "-loop", "0",
        ];
        var output = await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _ffmpeg.EncodeAsync(
            inputPath, options, temp, "gif", duration, MediaJob.MaxOutputBytes(inputPath, estimate, desired), MediaJob.Percent(progress), token,
            VideoEncoding.Slowness(video), VideoEncoding.DecoderThreadsFor(video), boundTime: true), ct)
            .ConfigureAwait(false);
        if (cut)
            OutputNote.Add(FirstSecondsNote);
        return output;
    }
}
