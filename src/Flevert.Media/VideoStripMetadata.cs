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
            .. VideoEncoding.Maps(info, audio), "-c", "copy", .. video.Codec == "h264" ? DropSei : [],
            "-map_metadata", "-1", "-map_chapters", "-1",
            .. info.Tags.TryGetValue("creation_time", out var date) && date.Length > 0 && !date.Contains('\0', StringComparison.Ordinal)
                ? (string[])["-metadata", $"creation_time={date}"]
                : [],
            .. MediaJob.LanguageOptions(audio), .. muxer.Options,
        ];
        // The same streams in less metadata: never much more than the input (MPEG-TS packets of 188 bytes become 192).
        return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _ffmpeg.EncodeAsync(
            inputPath, options, temp, muxer.Muxer, info.Duration, (inputBytes * 105 / 100) + (10 * 1_000_000), MediaJob.Percent(progress), token), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// H.264's SEI messages: what decoding does not need, and where an encoder signs its settings and AVCHD camcorders
    /// keep the camera, the time and, with GPS, the place (Sony and Panasonic's "MDPM"), none of which ffprobe shows as
    /// tags (security review, SEC-2). The pictures decode the same without them; the packets are no longer byte for
    /// byte the input's. HEVC keeps its SEI: it carries the HDR mastering data, and phones do not put places there.
    /// </summary>
    private static readonly string[] DropSei = ["-bsf:v", "filter_units=remove_types=6"];

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
