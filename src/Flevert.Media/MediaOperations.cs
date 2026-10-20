using Flevert.Core;

namespace Flevert.Media;

/// <summary>
/// The video and audio operations offered in the menu, in menu order (docs/specs/media.md, "Menu"): for a video, Compress
/// (the first user story: too heavy to send), Convert to, Extract audio, Remove hidden info; for audio, Convert to. Nothing
/// here starts ffmpeg: an operation probes its file when it runs.
/// </summary>
public static class MediaOperations
{
    public static IReadOnlyList<IMenuOperation> All { get; } = Create(Ffmpeg.NextToApp);

    internal static IReadOnlyList<IMenuOperation> Create(Ffmpeg ffmpeg) =>
    [
        .. CompressLevel.All.Select(level => new VideoCompress(level, ffmpeg)),
        new VideoCompressUnder(10, ffmpeg),
        new VideoCompressUnder(25, ffmpeg),
        new VideoCompressUnder(100, ffmpeg),
        .. VideoFormat.All.Select(format => new VideoConvert(format, ffmpeg)),
        new VideoToGif(ffmpeg),
        new AudioConvert(AudioFormat.Mp3, ffmpeg, fromVideo: true),
        new AudioConvert(AudioFormat.M4a, ffmpeg, fromVideo: true),
        new VideoStripMetadata(ffmpeg),
        .. AudioFormat.All.Select(format => new AudioConvert(format, ffmpeg)),
    ];
}
