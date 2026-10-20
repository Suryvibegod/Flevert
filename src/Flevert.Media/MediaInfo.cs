using System.Globalization;
using System.Text.Json;

namespace Flevert.Media;

/// <summary>What ffprobe found in a file, as far as the media operations need it.</summary>
/// <param name="Duration">Of the whole file; always more than zero once <see cref="MediaInfo.Parse"/> has accepted it.</param>
/// <param name="Video">The picture to convert: the first video stream that is not cover art, if any.</param>
/// <param name="Audio">Every audio stream, in file order; <see cref="MediaInfo.MainAudio"/> is the one converted alone.</param>
/// <param name="Tags">The file's own metadata (title, artist, creation_time…), keys in lower case.</param>
/// <param name="BitRate">Of the whole file in bits per second, when ffprobe knows it.</param>
public sealed record MediaInfo(
    TimeSpan Duration,
    VideoTrack? Video,
    IReadOnlyList<AudioTrack> Audio,
    IReadOnlyDictionary<string, string> Tags,
    long? BitRate)
{
    /// <summary>The longest file Flevert converts (docs/specs/media.md, "Bảo mật").</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);

    /// <summary>The largest picture Flevert converts: 8K, either way up (8192 × 4320 for cinema 8K).</summary>
    public const int MaxSide = 8192;

    public const long MaxPixels = 8192L * 4320;

    /// <summary>The default audio stream, or the first; <c>null</c> when the file has no sound.</summary>
    public AudioTrack? MainAudio => Audio.FirstOrDefault(a => a.IsDefault) ?? (Audio.Count > 0 ? Audio[0] : null);

    /// <summary>
    /// Reads ffprobe's <c>-print_format json -show_format -show_streams</c> output and refuses a file Flevert should not
    /// convert: no video or audio stream, no duration or one past <see cref="MaxDuration"/>, a picture past 8K.
    /// </summary>
    /// <exception cref="MediaException">The file is refused; the message says why.</exception>
    public static MediaInfo Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException e)
        {
            throw new MediaException(MediaException.UnreadableMessage, e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new MediaException(MediaException.UnreadableMessage);
            var format = Property(root, "format");
            VideoTrack? video = null;
            var audio = new List<AudioTrack>();
            var streamDuration = 0.0;
            if (Property(root, "streams") is { ValueKind: JsonValueKind.Array } streams)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.ValueKind != JsonValueKind.Object || Integer(stream, "index") is not { } index)
                        continue;
                    streamDuration = Math.Max(streamDuration, Number(stream, "duration") ?? 0);
                    switch (Text(stream, "codec_type"))
                    {
                        case "video" when video is null && !Disposition(stream, "attached_pic"):
                            video = ReadVideo(stream, index);
                            break;
                        case "audio":
                            audio.Add(new AudioTrack((int)index, Text(stream, "codec_name") ?? "", (int)(Integer(stream, "channels") ?? 0),
                                (int)(Number(stream, "sample_rate") ?? 0), (long?)Number(stream, "bit_rate"), Disposition(stream, "default"),
                                Text(Property(stream, "tags"), "language")));
                            break;
                    }
                }
            }

            if (video is null && audio.Count == 0)
                throw new MediaException(MediaException.NoStreamMessage);
            var seconds = Number(format, "duration") ?? streamDuration;
            if (double.IsNaN(seconds) || seconds <= 0)
                throw new MediaException(MediaException.UnknownDurationMessage);
            if (seconds > MaxDuration.TotalSeconds)
                throw new MediaException(MediaException.TooLongMessage);
            if (video is { } v && (v.Width > MaxSide || v.Height > MaxSide || (long)v.Width * v.Height > MaxPixels))
                throw new MediaException(MediaException.TooLargeVideoMessage);
            return new MediaInfo(TimeSpan.FromSeconds(seconds), video, audio, ReadTags(Property(format, "tags")), (long?)Number(format, "bit_rate"));
        }
    }

    private static VideoTrack ReadVideo(JsonElement stream, long index)
    {
        var width = (int)Math.Clamp(Integer(stream, "width") ?? 0, 0, int.MaxValue);
        var height = (int)Math.Clamp(Integer(stream, "height") ?? 0, 0, int.MaxValue);
        if (width == 0 || height == 0)
            throw new MediaException(MediaException.UnreadableMessage);
        var transfer = Text(stream, "color_transfer");
        return new VideoTrack((int)index, Text(stream, "codec_name") ?? "", width, height, Rotation(stream),
            transfer is "smpte2084" or "arib-std-b67", Rate(Text(stream, "avg_frame_rate")) ?? Rate(Text(stream, "r_frame_rate")) ?? 0);
    }

    // The display matrix in the stream's side data (ffprobe 5 and later), or the old "rotate" tag: the angle the picture
    // turns when shown, which ffmpeg applies when it encodes again.
    private static int Rotation(JsonElement stream)
    {
        double? angle = null;
        if (Property(stream, "side_data_list") is { ValueKind: JsonValueKind.Array } sideData)
        {
            foreach (var item in sideData.EnumerateArray())
                angle ??= Number(item, "rotation");
        }

        angle ??= Number(Property(stream, "tags"), "rotate");
        var degrees = (int)Math.Round(angle ?? 0) % 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static Dictionary<string, string> ReadTags(JsonElement? tags)
    {
        var read = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tags is not { ValueKind: JsonValueKind.Object } all)
            return read;
        foreach (var tag in all.EnumerateObject())
        {
            if (tag.Value.ValueKind == JsonValueKind.String)
                read.TryAdd(tag.Name.ToLowerInvariant(), tag.Value.GetString()!);
        }

        return read;
    }

    private static double? Rate(string? fraction)
    {
        if (fraction?.Split('/') is not [var top, var bottom]
            || !double.TryParse(top, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            || !double.TryParse(bottom, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0 || n <= 0)
            return null;
        return n / d;
    }

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value : null;

    private static string? Text(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    // ffprobe writes some numbers as JSON numbers (width) and others as strings (duration, bit_rate).
    private static double? Number(JsonElement? element, string name) => Property(element, name) switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetDouble(out var d) && double.IsFinite(d) => d,
        { ValueKind: JsonValueKind.String } s when double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d) => d,
        _ => null,
    };

    private static long? Integer(JsonElement? element, string name) =>
        Number(element, name) is { } d && d >= 0 && d <= int.MaxValue ? (long)d : null;

    private static bool Disposition(JsonElement stream, string name) => Number(Property(stream, "disposition"), name) is > 0;
}

/// <param name="Index">The stream's index in the file, for <c>-map 0:&lt;index&gt;</c>.</param>
/// <param name="Width">As stored, before <paramref name="Rotation"/>.</param>
/// <param name="Rotation">0, 90, 180 or 270 (or another angle a file claims): how far the picture turns when shown.</param>
/// <param name="IsHdr">PQ (HDR10, Dolby Vision) or HLG, as iPhones record: needs tone mapping to look right in 8-bit SDR.</param>
/// <param name="FrameRate">Average frames per second; 0 when unknown.</param>
public sealed record VideoTrack(int Index, string Codec, int Width, int Height, int Rotation, bool IsHdr, double FrameRate)
{
    private bool Sideways => Rotation is 90 or 270;

    /// <summary>The width as shown, once turned.</summary>
    public int DisplayWidth => Sideways ? Height : Width;

    public int DisplayHeight => Sideways ? Width : Height;
}

/// <param name="Language">The ISO 639-2 code the file gives, such as <c>vie</c>, if any.</param>
public sealed record AudioTrack(int Index, string Codec, int Channels, int SampleRate, long? BitRate, bool IsDefault, string? Language);
