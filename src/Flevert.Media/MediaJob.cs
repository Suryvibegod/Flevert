using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Flevert.Core;

namespace Flevert.Media;

/// <summary>What every media operation does around ffmpeg: names, limits, metadata and progress.</summary>
internal static partial class MediaJob
{
    /// <summary>The source types of docs/specs/media.md, "Thao tác". <c>.gif</c> stays an image (module image).</summary>
    public static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".webm", ".avi", ".wmv", ".3gp", ".mts", ".m2ts"];

    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".aif"];

    // The file's own tags that are copied, by name; everything else is dropped, which is how GPS (location, ©xyz,
    // com.apple.quicktime.location.*) never reaches an output, whatever the container writes (MKV writes every tag).
    private static readonly string[] KeptTags =
        ["title", "artist", "album", "album_artist", "composer", "genre", "date", "track", "disc", "comment", "copyright", "creation_time"];

    private const int MaxTagChars = 1_000;
    private const long Megabyte = 1_000_000;

    /// <summary>Room left on the disk beyond the estimate, so a conversion never fills it to the last byte.</summary>
    private const long SpareBytes = 50 * Megabyte;

    /// <summary><c>clip.mov</c> with <paramref name="suffixAndExtension"/> " (compressed).mp4" → <c>clip (compressed).mp4</c> beside it.</summary>
    public static string Beside(string inputPath, string suffixAndExtension) =>
        Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + suffixAndExtension);

    /// <summary>
    /// The options that replace the input's metadata by the tags Flevert keeps: no GPS, no chapters, the main audio's
    /// language of each sound stream written (<paramref name="audio"/>, in output order) when the file names one. A tag
    /// the file does not have is taken from the first of them, as Ogg and Opus files keep theirs on the stream; written as
    /// the file's tags, every muxer puts them where its players look.
    /// </summary>
    public static IEnumerable<string> MetadataOptions(MediaInfo info, IReadOnlyList<AudioTrack> audio)
    {
        var first = audio.Count > 0 ? audio[0] : null;
        yield return "-map_metadata";
        yield return "-1";
        yield return "-map_chapters";
        yield return "-1";
        foreach (var name in KeptTags)
        {
            // A value is one argument, never parsed by a shell; a null character would end it early, so it is left out.
            if (!info.Tags.TryGetValue(name, out var value) && first?.Tags.TryGetValue(name, out value) != true)
                continue;
            if (value is not { Length: > 0 } || value.Contains('\0', StringComparison.Ordinal))
                continue;
            yield return "-metadata";
            yield return $"{name}={Cut(value)}";
        }

        foreach (var option in LanguageOptions(audio))
            yield return option;
    }

    /// <summary>The language of each sound stream written, in output order, when the file names one in ISO 639-2.</summary>
    public static IEnumerable<string> LanguageOptions(IReadOnlyList<AudioTrack> audio)
    {
        for (var i = 0; i < audio.Count; i++)
        {
            if (audio[i].Language is { } language && Language().IsMatch(language) && language != "und")
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"-metadata:s:a:{i}");
                yield return $"language={language}";
            }
        }
    }

    private static string Cut(string value)
    {
        if (value.Length <= MaxTagChars)
            return value;
        var end = char.IsHighSurrogate(value[MaxTagChars - 1]) ? MaxTagChars - 1 : MaxTagChars;
        return value[..end];
    }

    [GeneratedRegex(@"^[a-z]{3}\z")]
    private static partial Regex Language();

    /// <summary>
    /// The size past which ffmpeg stops writing (<c>-fs</c>): four times the input plus 100 MB (docs/specs/media.md,
    /// "Bảo mật"), or twice the size expected from the duration if more, since a short MP3 legitimately becomes a WAV
    /// ten times its size. A file that decodes to far more than it claims stops there instead of filling the disk.
    /// </summary>
    /// <remarks>
    /// Never more than the room on the output's disk past <see cref="SpareBytes"/>: an output that would need more fails
    /// there instead of filling the disk for every other program (SEC-1).
    /// </remarks>
    public static long MaxOutputBytes(string inputPath, long estimatedBytes, string outputPath)
    {
        var limit = Math.Max(4 * new FileInfo(inputPath).Length + 100 * Megabyte, 2 * estimatedBytes);
        return WithinFreeSpace(limit, outputPath);
    }

    /// <summary><paramref name="limit"/>, or the room on the disk of <paramref name="outputPath"/> past the spare, if less.</summary>
    public static long WithinFreeSpace(long limit, string outputPath) =>
        FreeBytes(outputPath) is { } free ? Math.Clamp(free - SpareBytes, Math.Min(Megabyte, limit), limit) : limit;

    /// <summary>The size of <paramref name="duration"/> at <paramref name="bitsPerSecond"/>.</summary>
    public static long Bytes(TimeSpan duration, long bitsPerSecond) => (long)(duration.TotalSeconds * bitsPerSecond / 8);

    /// <summary>Fails before anything runs when the output's disk has less room than the estimate (docs/specs/media.md, core item 3).</summary>
    /// <exception cref="MediaException">Not enough room.</exception>
    public static void RequireSpace(string outputPath, long estimatedBytes)
    {
        if (FreeBytes(outputPath) is { } free && free < estimatedBytes + SpareBytes)
            throw new MediaException(MediaException.NotEnoughSpaceMessage(estimatedBytes + SpareBytes));
    }

    // The room this user may still write on the disk of a path, quotas included, or null when Windows does not say.
    // GetDiskFreeSpaceEx, not DriveInfo: DriveInfo refuses a network share and a \\?\ path, which then had no check
    // and no -fs bound, and a full share failed as a damaged file (doubt review, 2026-10-21).
    private static long? FreeBytes(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder is null)
                return null;
            if (!OperatingSystem.IsWindows())
                return new DriveInfo(folder).AvailableFreeSpace;
            return GetDiskFreeSpaceExW(folder, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    /// <summary>
    /// ffmpeg's progress from 0 to 1, shown as the part from <paramref name="from"/> to <paramref name="to"/> of the file
    /// (two passes take 0–0.5 and 0.5–1), reported only when the whole percent changes.
    /// </summary>
    public static IProgress<double>? Percent(IProgress<FileProgress>? progress, double from = 0, double to = 1) =>
        progress is null ? null : new PercentProgress(progress, from, to);

    private sealed class PercentProgress(IProgress<FileProgress> progress, double from, double to) : IProgress<double>
    {
        private int _last = -1;

        public void Report(double value)
        {
            var percent = (int)Math.Floor((from + (Math.Clamp(value, 0, 1) * (to - from))) * 100);
            if (percent <= _last)
                return;
            _last = percent;
            progress.Report(FileProgress.Percent(percent));
        }
    }
}
