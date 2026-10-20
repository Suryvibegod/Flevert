using System.Globalization;
using Flevert.Core;

namespace Flevert.Media;

/// <summary>The options every video output shares: which streams, the picture's filters, and the H.264 settings.</summary>
internal static class VideoEncoding
{
    /// <summary>
    /// HDR (PQ or HLG) to SDR: to linear light, BT.2020 to BT.709 primaries, Hable tone mapping, then BT.709 TV range.
    /// Without it an iPhone's HDR video looks washed out once it is 8-bit H.264 (docs/specs/media.md).
    /// </summary>
    internal const string ToneMap =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv";

    /// <summary>
    /// The picture as shown (ffmpeg turns it upright first), at most <paramref name="maxShortSide"/> on its short side and
    /// <paramref name="maxLongSide"/> on its long one, sides even.
    /// </summary>
    public static (int Width, int Height) OutputSize(VideoTrack video, int? maxShortSide = null, int? maxLongSide = null)
    {
        double width = video.DisplayWidth, height = video.DisplayHeight;
        var shortSide = Math.Min(width, height);
        if (maxShortSide is { } max && shortSide > max)
        {
            width *= max / shortSide;
            height *= max / shortSide;
        }

        var longSide = Math.Max(width, height);
        if (maxLongSide is { } maxLong && longSide > maxLong)
        {
            width *= maxLong / longSide;
            height *= maxLong / longSide;
        }

        // 4:2:0 needs even sides: rounded down, so a picture is never enlarged; never 0.
        return (Math.Max(2, (int)(width / 2) * 2), Math.Max(2, (int)(height / 2) * 2));
    }

    /// <summary>
    /// <c>-vf</c> for 8-bit 4:2:0 output of <paramref name="size"/>: scaled when it differs from the picture as shown,
    /// tone mapped when the input is HDR. ffmpeg turns the picture upright before these filters (autorotate) and drops the
    /// rotation from the output, so players that ignore it show it upright too.
    /// </summary>
    public static string[] Filters(VideoTrack video, (int Width, int Height) size, string? extra = null)
    {
        var filters = new List<string>();
        if (extra is not null)
            filters.Add(extra);
        if (size != (video.DisplayWidth, video.DisplayHeight))
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"scale={size.Width}:{size.Height}:flags=lanczos"));
        if (video.IsHdr)
            filters.Add(ToneMap);
        filters.Add("format=yuv420p");
        return ["-vf", string.Join(',', filters)];
    }

    /// <summary>
    /// The sound an output carries: the file's first audio stream, or none (docs/specs/media.md). The notification says
    /// when others were left out, since they may be other languages.
    /// </summary>
    public static IReadOnlyList<AudioTrack> FirstAudio(MediaInfo info)
    {
        if (info.Audio.Count > 1)
            OutputNote.Add(string.Create(CultureInfo.InvariantCulture, $"Only the first of its {info.Audio.Count} sound tracks was kept."));
        return info.MainAudio is { } first ? [first] : [];
    }

    /// <summary>The picture, then the sound, by their index in the input.</summary>
    public static string[] Maps(MediaInfo info, IReadOnlyList<AudioTrack> audio) =>
        ["-map", $"0:{info.Video!.Index}", .. audio.SelectMany(track => (string[])["-map", $"0:{track.Index}"])];

    /// <summary>x264 at preset <c>medium</c> with constant quality <paramref name="crf"/>: plays on every phone and PC.</summary>
    public static string[] H264(int crf) =>
        ["-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(CultureInfo.InvariantCulture)];

    /// <summary>AAC at <paramref name="kbps"/> for each sound stream.</summary>
    public static string[] Aac(int kbps) => ["-c:a", "aac", "-b:a", string.Create(CultureInfo.InvariantCulture, $"{kbps}k")];

    /// <summary>
    /// About how large the output gets, for the disk-space check and the size limit: 0.15 bits per pixel and frame, which
    /// H.264 at CRF 20 stays under for camera video, plus the sound.
    /// </summary>
    public static long EstimateBytes(MediaInfo info, (int Width, int Height) size, int audioKbps, int audioStreams)
    {
        var fps = info.Video!.FrameRate is > 0 and < 240 ? info.Video.FrameRate : 30;
        var videoBits = (long)(size.Width * (double)size.Height * fps * 0.15);
        return MediaJob.Bytes(info.Duration, videoBits + (audioKbps * 1000L * audioStreams));
    }
}
