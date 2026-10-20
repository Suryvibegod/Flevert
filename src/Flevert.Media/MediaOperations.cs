using Flevert.Core;
using Flevert.Core.Menu;

namespace Flevert.Media;

/// <summary>
/// The video and audio operations offered in the menu, in menu order (docs/specs/media.md, "Menu"): for a video, Compress
/// (the first user story: too heavy to send), Convert to, Extract audio, Remove hidden info; for audio, Convert to. Nothing
/// here starts ffmpeg: an operation probes its file when it runs.
/// </summary>
public static class MediaOperations
{
    public static IReadOnlyList<IMenuOperation> All { get; } = Create(Ffmpeg.NextToApp);

    /// <summary>
    /// The icon and group of each entry at the top of the video and audio menus (docs/specs/media.md, "Menu"; the menu
    /// principles of docs/specs/pdf.md, v1.2.2): 1 changing the file (Compress, Convert to, Extract audio) · 2 privacy
    /// (Remove hidden info), after a separator. The labels pictures and PDFs share look as they do there
    /// (<see cref="MenuEntryLook.Combine"/> checks it); the choices inside a submenu have no icon.
    /// </summary>
    public static IReadOnlyDictionary<string, MenuEntryLook> Looks { get; } = new Dictionary<string, MenuEntryLook>(StringComparer.Ordinal)
    {
        ["Compress"] = new("arrow_minimize", 1),
        ["Convert to"] = new("arrow_swap", 1),
        ["Extract audio"] = new("music_note_2", 1),
        ["Remove hidden info"] = new("shield", 2),
    };

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
