using Flevert.Core;

namespace Flevert.Media;

/// <summary>
/// The video and audio operations offered in the menu, in menu order (docs/specs/media.md, "Menu"). Nothing here starts
/// ffmpeg: an operation probes its file when it runs.
/// </summary>
public static class MediaOperations
{
    public static IReadOnlyList<IMenuOperation> All { get; } = Create(Ffmpeg.NextToApp);

    internal static IReadOnlyList<IMenuOperation> Create(Ffmpeg ffmpeg) =>
    [
        .. VideoFormat.All.Select(format => new VideoConvert(format, ffmpeg)),
        .. CompressLevel.All.Select(level => new VideoCompress(level, ffmpeg)),
        new VideoCompressUnder(10, ffmpeg),
        new VideoCompressUnder(25, ffmpeg),
        new VideoCompressUnder(100, ffmpeg),
        new AudioConvert(AudioFormat.Mp3, ffmpeg, fromVideo: true),
        new AudioConvert(AudioFormat.M4a, ffmpeg, fromVideo: true),
        .. AudioFormat.All.Select(format => new AudioConvert(format, ffmpeg)),
    ];
}
