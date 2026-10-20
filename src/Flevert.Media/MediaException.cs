using System.Globalization;

namespace Flevert.Media;

/// <summary>A video or audio file Flevert cannot convert, with a message for the user; ffmpeg's own words are in the inner exception.</summary>
public sealed class MediaException : Exception
{
    public MediaException() { }

    public MediaException(string message) : base(message) { }

    public MediaException(string message, Exception innerException) : base(message, innerException) { }

    internal const string UnreadableMessage =
        "Flevert cannot read this file as video or audio: it is damaged, or of a kind Flevert does not convert.";

    internal const string NoStreamMessage = "This file holds no video or sound Flevert can read.";

    internal const string NoVideoMessage = "This file holds no video.";

    internal const string NoSoundMessage = "This file has no sound.";

    internal const string NoSoundToExtractMessage = "This video has no sound to extract.";

    internal const string UnknownDurationMessage = "Flevert cannot tell how long this file is: it may be damaged.";

    internal const string TooLongMessage = "This file is longer than 12 hours, more than Flevert converts.";

    internal const string TooLargeVideoMessage = "This video is larger than 8K, more than Flevert converts.";

    internal const string ToolFailedMessage =
        "Flevert's video tool stopped on this file without finishing: the file may be damaged, or made to crash video tools.";

    internal const string TooSlowMessage = "Converting this file took too long: it may be damaged, or made to stall video tools.";

    internal const string NeedsTooMuchMemoryMessage =
        "This file needs more memory than Flevert allows its video tool (4 GB): it is too large, or made to exhaust memory.";

    internal const string LongerThanItSaysMessage =
        "This file holds far more than its header says, so Flevert stopped: it may be damaged, or made to fill the disk.";

    internal const string TooBigOutputMessage =
        "The converted file grew far larger than expected, or than the free disk space allows, so Flevert stopped: the file may be damaged.";

    internal static string NotEnoughSpaceMessage(long neededBytes) => string.Create(CultureInfo.InvariantCulture,
        $"Not enough free disk space: converting this file needs about {Math.Ceiling(neededBytes / 1_000_000.0):0} MB.");
}

/// <summary>ffmpeg or ffprobe failed in a way Flevert has no better words for; the message holds the tool's own.</summary>
public sealed class FfmpegException : Exception
{
    public FfmpegException() { }

    public FfmpegException(string message) : base(message) { }

    public FfmpegException(string message, Exception innerException) : base(message, innerException) { }

    public FfmpegException(string tool, int exitCode, string detail)
        : base(string.Create(CultureInfo.InvariantCulture, $"{tool} failed (exit code {exitCode}): {detail}")) => ExitCode = exitCode;

    public int ExitCode { get; }
}
