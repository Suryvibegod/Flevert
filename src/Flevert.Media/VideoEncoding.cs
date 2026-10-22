using System.Globalization;
using Flevert.Core;

namespace Flevert.Media;

/// <summary>The options every video output shares: which streams, the picture's filters, and the H.264 settings.</summary>
internal static class VideoEncoding
{
    /// <summary>
    /// One frame per frame, from its two fields, for frames marked interlaced only (an AVCHD camcorder's 1080i, DV):
    /// encoded as they are, they show combs on motion, and scaled, ghosts (code review, 2026-10-21).
    /// </summary>
    internal const string Deinterlace = "bwdif=mode=send_frame:deint=interlaced";

    /// <summary>
    /// HDR (PQ or HLG) to SDR: to linear light, BT.2020 to BT.709 primaries, Mobius tone mapping, then BT.709 TV range.
    /// Without it an iPhone's HDR video looks washed out once it is 8-bit H.264 (docs/specs/media.md). Mobius, not Hable:
    /// an SDR picture made HLG or PQ and mapped back kept a mean luma of 122 of 126 with it, 103 with Hable, which made
    /// every HDR video a fifth darker (doubt review, 2026-10-21).
    /// </summary>
    internal const string ToneMap =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:desat=0,zscale=t=bt709:m=bt709:r=tv";

    /// <summary>
    /// The short side of the largest picture Flevert encodes: 4K. An 8K phone video encoded at its size took more than
    /// the 4 GB cap within two seconds on 16 cores (HLG, x264 medium, measured 2026-10-21; 4K at 60 peaked at 3.4 GB), so
    /// 8K comes out at 4K, which every screen and player shows.
    /// </summary>
    internal const int MaxShortSide = 2160;

    /// <summary>
    /// The picture as shown (ffmpeg turns it upright first), at most <paramref name="maxShortSide"/> (and never more than
    /// <see cref="MaxShortSide"/>) on its short side and <paramref name="maxLongSide"/> on its long one, sides even.
    /// </summary>
    public static (int Width, int Height) OutputSize(VideoTrack video, int? maxShortSide = null, int? maxLongSide = null)
    {
        double width = video.DisplayWidth, height = video.DisplayHeight;
        var shortSide = Math.Min(width, height);
        var max = Math.Min(maxShortSide ?? MaxShortSide, MaxShortSide);
        if (shortSide > max)
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
    /// <c>-vf</c> for 8-bit 4:2:0 output of <paramref name="size"/>: deinterlaced when stored in fields, scaled when it
    /// differs from the picture as shown, tone mapped when the input is HDR. ffmpeg turns the picture upright before these
    /// filters (autorotate) and drops the rotation from the output, so players that ignore it show it upright too.
    /// </summary>
    public static string[] Filters(VideoTrack video, (int Width, int Height) size)
    {
        var filters = new List<string>();
        // Not when turned: autorotate's transpose comes first, and a turned picture's fields are columns (very rare).
        if (video.IsInterlaced && video.Rotation == 0)
            filters.Add(Deinterlace);
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

    /// <summary>
    /// Each frame keeps its own time, as the input has it. Without it ffmpeg chooses by the output's container: as it is
    /// for x264's first pass (no container), constant frame rate for MP4, so a phone's variable frame rate gets frames
    /// added in the second pass that the first never saw, and with ffmpeg 6.1 that second pass hung (code review, CR-3).
    /// </summary>
    public static readonly string[] FrameTimes = ["-fps_mode", "passthrough"];

    /// <summary>
    /// How many times the duration a pass of <paramref name="video"/> at <paramref name="size"/> may take: six for 1080p
    /// at 30 frames a second (docs/specs/media.md), more for more pixels a second (4K at 60 is eight times as many), twice
    /// that for HDR, which is tone mapped in floating point, and <paramref name="factor"/> times that for a slower encoder.
    /// A timeout only stops a run that is stuck; Cancel stops the others.
    /// </summary>
    internal const double MaxSlowness = 200;

    public static double Slowness(VideoTrack video, double factor = 1)
    {
        var fps = video.FrameRate is > 0 and < 240 ? video.FrameRate : 30;
        var pixelRate = (double)video.Width * video.Height * fps / (1920.0 * 1080 * 30);
        // Bounded: a header claiming 8K at 239 fps would otherwise wait for years (re-check review, RC-5).
        return Math.Min(MaxSlowness, Ffmpeg.DefaultSlowness * factor * Math.Max(1, pixelRate) * (video.IsHdr ? 2 : 1));
    }

    /// <summary>The picture, then the sound, by their index in the input.</summary>
    public static string[] Maps(MediaInfo info, IReadOnlyList<AudioTrack> audio) =>
        ["-map", $"0:{info.Video!.Index}", .. audio.SelectMany(track => (string[])["-map", $"0:{track.Index}"])];

    /// <summary>x264 at preset <c>medium</c> with constant quality <paramref name="crf"/>: plays on every phone and PC.</summary>
    public static string[] H264(int crf) =>
        ["-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(CultureInfo.InvariantCulture), .. EncoderThreads];

    /// <summary>
    /// x264's threads: 1.5 per core as it chooses itself, but at most 12. Each holds frames, so memory grows with them: 3 s
    /// of 4K60 HLG peaked at 4.5 GB with the 24 x264 and 13 decoder threads a 16-thread PC gets, past the
    /// <see cref="Ffmpeg.MaxMemoryBytes"/> cap, and at 3.2 GB with 12 and 8, as fast (re-check review, RC-2, 2026-10-20).
    /// </summary>
    public static string[] EncoderThreads => ["-threads", Math.Clamp(Environment.ProcessorCount * 3 / 2, 1, 12).ToString(CultureInfo.InvariantCulture)];

    /// <summary>The decoder's threads, before the input: one per core as ffmpeg chooses, but at most 8 (<see cref="EncoderThreads"/>).</summary>
    public static string[] DecoderThreads => ["-threads", Math.Clamp(Environment.ProcessorCount, 1, 8).ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// <see cref="DecoderThreads"/>, or 2 for a picture larger than 4K: each of an 8K decoder's threads holds 100 MB
    /// frames. 8K HLG to 4K peaked at 4.6 GB with 8 and 12 threads, 3.3 GB with 2 and 6 (16 cores, 2026-10-21).
    /// </summary>
    public static string[] DecoderThreadsFor(VideoTrack video) => IsLarger(video) ? ["-threads", "2"] : DecoderThreads;

    /// <summary>
    /// After the codec's options for a picture larger than 4K: x264 or VP9 with 6 threads (<see cref="DecoderThreadsFor"/>);
    /// ffmpeg takes the last <c>-threads</c> of a stream. Nothing otherwise.
    /// </summary>
    public static string[] EncoderThreadsFor(VideoTrack video) => IsLarger(video) ? ["-threads", "6"] : [];

    private static bool IsLarger(VideoTrack video) => Math.Min(video.DisplayWidth, video.DisplayHeight) > MaxShortSide;

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
