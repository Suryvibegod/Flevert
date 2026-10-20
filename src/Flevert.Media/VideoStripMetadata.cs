using Flevert.Core;

namespace Flevert.Media;

/// <summary>
/// <c>Remove hidden info</c> for a video: writes <c>&lt;name&gt; (no metadata).&lt;ext&gt;</c> beside it, in the same
/// container, with the picture and the sound copied as they are (no re-encoding: as fast as copying the file, nothing
/// lost) and none of the metadata a phone or camera wrote, of the file or of any stream: where it was filmed, the camera
/// model, the software. Only the recording date stays, and the rotation the picture is shown with (docs/specs/media.md).
/// Subtitles, data streams (an iPhone's timed metadata), attachments, cover art and other video or sound streams are
/// left out. A video with nothing of this is skipped.
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
        if (!HasHiddenInfo(info))
            throw new OperationSkippedException(NothingToRemoveMessage);
        var inputBytes = new FileInfo(inputPath).Length;
        var desired = MediaJob.Beside(inputPath, " (no metadata)" + extension);
        MediaJob.RequireSpace(desired, inputBytes);
        string[] options =
        [
            .. VideoEncoding.Maps(info, info.MainAudio is { } audio ? [audio] : []), "-c", "copy",
            "-map_metadata", "-1", "-map_chapters", "-1",
            .. info.Tags.TryGetValue("creation_time", out var date) && date.Length > 0 && !date.Contains('\0', StringComparison.Ordinal)
                ? (string[])["-metadata", $"creation_time={date}"]
                : [],
            .. muxer.Options,
        ];
        // The same streams in less metadata: never more than the input and a little room.
        return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _ffmpeg.EncodeAsync(
            inputPath, options, temp, muxer.Muxer, info.Duration, inputBytes + (10 * 1_000_000), MediaJob.Percent(progress), token), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Metadata a phone or camera wrote, or a stream that is left out, which may carry it.</summary>
    internal static bool HasHiddenInfo(MediaInfo info) =>
        info.OtherStreams > 0 || info.Audio.Count > 1
        || info.Tags.Keys.Concat(info.Video?.Tags.Keys ?? []).Concat(info.Audio.SelectMany(a => a.Tags.Keys)).Any(key => !Plain.Contains(key));
}
