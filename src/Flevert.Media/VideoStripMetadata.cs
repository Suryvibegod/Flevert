using System.Globalization;
using Flevert.Core;

namespace Flevert.Media;

/// <summary>
/// <c>Remove hidden info</c> for a video: writes <c>&lt;name&gt; (no metadata).&lt;ext&gt;</c> beside it, in the same
/// container, with the picture and the sound copied as they are (no re-encoding: as fast as copying the file, nothing
/// lost; only H.264's SEI messages are dropped, <see cref="DropSei"/>) and none of the metadata a phone or camera wrote,
/// of the file or of any stream: where it was filmed, the camera model, the software. Only the recording date, the
/// sound's language and the rotation the picture is shown with stay (docs/specs/media.md). Subtitles, data streams (an
/// iPhone's timed metadata), attachments, cover art and other video or sound streams are left out, and the notification
/// says when other sound tracks were. A video known to hold nothing of this is skipped.
/// </summary>
public sealed class VideoStripMetadata : IOperation
{
    internal const string NothingToRemoveMessage = "This video has no hidden info to remove.";

    // The muxer for each source type, the same container as the input, and its options.
    private static readonly Dictionary<string, (string Muxer, string[] Options)> Muxers = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = ("mp4", []),
        [".m4v"] = ("mp4", []),
        [".mov"] = ("mov", []),
        [".3gp"] = ("3gp", []),
        [".mkv"] = ("matroska", []),
        [".webm"] = ("webm", []),
        [".avi"] = ("avi", []),
        [".wmv"] = ("asf", []),
        // AVCHD and Blu-ray write MPEG-TS in 192-byte packets.
        [".mts"] = ("mpegts", ["-mpegts_m2ts_mode", "1"]),
        [".m2ts"] = ("mpegts", ["-mpegts_m2ts_mode", "1"]),
    };

    // Tags that say nothing about who filmed what, where or with what: what containers and ffmpeg itself write (the
    // brand of an MP4, a stream's language and handler, Matroska's statistics). The date is kept.
    private static readonly HashSet<string> Plain = new(StringComparer.OrdinalIgnoreCase)
    {
        "major_brand", "minor_version", "compatible_brands", "creation_time", "encoder", "duration", "language", "handler_name",
        "vendor_id", "bps", "number_of_frames", "number_of_bytes", "_statistics_writing_app", "_statistics_writing_date_utc",
        "_statistics_tags",
    };

    private readonly Ffmpeg _ffmpeg;

    internal VideoStripMetadata(Ffmpeg ffmpeg) => _ffmpeg = ffmpeg;

    public string Id => "video.strip-metadata";

    public string MenuPath => "Remove hidden info";

    public IReadOnlyCollection<string> Extensions => MediaJob.VideoExtensions;

    // Copying is mostly reading and writing.
    public int MaxParallel => 2;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var info = await _ffmpeg.ProbeAsync(inputPath, ct).ConfigureAwait(false);
        var video = info.Video ?? throw new MediaException(MediaException.NoVideoMessage);
        var extension = Path.GetExtension(inputPath);
        if (!Muxers.TryGetValue(extension, out var muxer))
            throw new MediaException(MediaException.UnreadableMessage);
        if (!MayHoldHiddenInfo(info, extension))
            throw new OperationSkippedException(NothingToRemoveMessage);
        var audio = VideoEncoding.FirstAudio(info);
        var inputBytes = new FileInfo(inputPath).Length;
        var desired = MediaJob.Beside(inputPath, " (no metadata)" + extension);
        MediaJob.RequireSpace(desired, inputBytes);
        string[] options =
        [
            .. VideoEncoding.Maps(info, audio), "-c", "copy", .. DropsSei(inputPath, extension, video) ? DropSei : [],
            "-map_metadata", "-1", "-map_chapters", "-1",
            .. info.Tags.TryGetValue("creation_time", out var date) && IsDate(date) ? (string[])["-metadata", $"creation_time={date}"] : [],
            .. MediaJob.LanguageOptions(audio), .. muxer.Options,
        ];
        // The same streams in less metadata: never much more than the input (MPEG-TS packets of 188 bytes become 192).
        return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _ffmpeg.EncodeAsync(
            inputPath, options, temp, muxer.Muxer, info.Duration, MediaJob.WithinFreeSpace((inputBytes * 105 / 100) + (10 * 1_000_000), desired),
            MediaJob.Percent(progress), token), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A date as files hold it (<c>2026-10-20T08:15:00.000000Z</c>), kept as it is; anything else is dropped, since a
    /// file chose it: 40 KB of it made the command line too long for Windows (code review, 2026-10-21).
    /// </summary>
    internal static bool IsDate(string value) =>
        value.Length is > 0 and <= 64 && !value.Contains('\0', StringComparison.Ordinal)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);

    /// <summary>
    /// H.264's SEI messages, where AVCHD camcorders keep the camera, the time and, with GPS, the place (Sony and
    /// Panasonic's "MDPM"), which ffprobe does not show as tags (security review, SEC-2). ffmpeg can only drop them all,
    /// and with them the recovery points a stream without regular keyframes (a dashcam's intra refresh) is seeked by
    /// (re-check review, RC-3), so only where a camcorder's data is (<see cref="DropsSei"/>). Pictures decoded from the
    /// start are the same; the packets are no longer byte for byte the input's. Other streams, whose SEI holds at most an
    /// encoder's settings, and HEVC, whose SEI holds the HDR mastering data, are copied whole.
    /// </summary>
    private static readonly string[] DropSei = ["-bsf:v", "filter_units=remove_types=6"];

    private static readonly HashSet<string> Avchd = new(StringComparer.OrdinalIgnoreCase) { ".mts", ".m2ts" };

    // The SEI user data AVCHD defines for MDPM: its UUID, then "MDPM". These bytes hold no two zero bytes H.264 would have
    // to escape, so they appear in the file as they are, and no picture holds all 20 by chance (RC3-3). Camcorders write
    // it in every GOP: the first megabytes show it, unless an MP4's index comes first and is larger (hours of video, RC3-2).
    private static readonly byte[] CamcorderData = [.. Convert.FromHexString("17ee8c60f84d11d98cd60800200c9a66"), .. "MDPM"u8];
    private const int CamcorderDataWindow = 8 * 1024 * 1024;

    /// <summary>
    /// Whether to drop an H.264 stream's SEI: in an AVCHD file, or in any file whose first megabytes hold a camcorder's
    /// MDPM, as an AVCHD clip remuxed to MP4 or MKV does (re-check review, RC2-1).
    /// </summary>
    internal static bool DropsSei(string inputPath, string extension, VideoTrack video)
    {
        if (video.Codec != "h264")
            return false;
        if (Avchd.Contains(extension))
            return true;
        using var file = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[64 * 1024];
        var carried = 0;
        for (var read = 0; read < CamcorderDataWindow;)
        {
            var count = file.Read(buffer, carried, buffer.Length - carried);
            if (count == 0)
                return false;
            read += count;
            var filled = carried + count;
            if (buffer.AsSpan(0, filled).IndexOf(CamcorderData) >= 0)
                return true;
            // A match may straddle two reads: keep the last bytes.
            carried = Math.Min(CamcorderData.Length - 1, filled);
            buffer.AsSpan(filled - carried, carried).CopyTo(buffer);
        }

        return false;
    }

    // Containers and streams that can hold what ffprobe does not show as tags: XMP and makers' own boxes in MOV and MP4
    // (a dashcam's GPS track), an AVCHD camcorder's data in its H.264 stream. Such a file always gets its copy, rather
    // than a skip that would claim it has nothing hidden.
    private static readonly HashSet<string> Opaque = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".3gp", ".mts", ".m2ts" };

    /// <summary>
    /// Metadata a phone or camera wrote, a stream that is left out and may carry it, or a file of a kind that can hold
    /// what ffprobe does not show (<see cref="Opaque"/>, H.264's SEI).
    /// </summary>
    internal static bool MayHoldHiddenInfo(MediaInfo info, string extension) =>
        Opaque.Contains(extension) || info.Video?.Codec == "h264" || info.OtherStreams > 0 || info.Audio.Count > 1
        || info.Tags.Keys.Concat(info.Video?.Tags.Keys ?? []).Concat(info.Audio.SelectMany(a => a.Tags.Keys)).Any(key => !Plain.Contains(key));
}
