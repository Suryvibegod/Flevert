using Flevert.Core;

namespace Flevert.Media;

/// <summary>An audio file type Flevert writes (docs/specs/media.md, table of operations).</summary>
internal sealed class AudioFormat
{
    private AudioFormat(string label, string extension, string muxer, Func<AudioTrack, string[]> codec, Func<AudioTrack, long> bitsPerSecond)
    {
        Label = label;
        Extension = extension;
        Muxer = muxer;
        Codec = codec;
        BitsPerSecond = bitsPerSecond;
    }

    /// <summary>LAME VBR quality 2 (about 190 kbit/s), which every player reads.</summary>
    public static AudioFormat Mp3 { get; } = new("MP3", ".mp3", "mp3", _ => ["-c:a", "libmp3lame", "-q:a", "2"], _ => 190_000);

    /// <summary>AAC 192 kbit/s; AAC already is kept as it is, not encoded again (no loss, no time).</summary>
    public static AudioFormat M4a { get; } = new("M4A", ".m4a", "ipod",
        audio => audio.Codec == "aac" ? ["-c:a", "copy", "-movflags", "+faststart"] : ["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
        audio => audio.Codec == "aac" ? Math.Max(audio.BitRate ?? 0, 192_000) : 192_000);

    /// <summary>16-bit PCM; past 4 GB (RIFF's limit) the header becomes RF64.</summary>
    public static AudioFormat Wav { get; } = new("WAV", ".wav", "wav", _ => ["-c:a", "pcm_s16le", "-rf64", "auto"], Pcm);

    /// <summary>FLAC at its default level 5; lossless, about 60 % of the PCM.</summary>
    public static AudioFormat Flac { get; } = new("FLAC", ".flac", "flac", _ => ["-c:a", "flac", "-compression_level", "5"], audio => Pcm(audio) * 6 / 10);

    /// <summary>Vorbis quality 5 (about 160 kbit/s).</summary>
    public static AudioFormat Ogg { get; } = new("OGG", ".ogg", "ogg", _ => ["-c:a", "libvorbis", "-q:a", "5"], _ => 160_000);

    /// <summary>Opus 128 kbit/s in Ogg, as <c>.opus</c> files are.</summary>
    public static AudioFormat Opus { get; } = new("Opus", ".opus", "opus", _ => ["-c:a", "libopus", "-b:a", "128k"], _ => 128_000);

    public static IReadOnlyList<AudioFormat> All { get; } = [Mp3, M4a, Wav, Flac, Ogg, Opus];

    /// <summary>As the menu shows it: "MP3", "Opus".</summary>
    public string Label { get; }

    public string Extension { get; }

    /// <summary>ffmpeg's <c>-f</c>: the output's temporary name ends in <c>.partial</c>, so the container is always named.</summary>
    public string Muxer { get; }

    /// <summary>The codec options for an input whose sound is <c>audio</c>.</summary>
    public Func<AudioTrack, string[]> Codec { get; }

    /// <summary>About how many bits a second of output takes, for the disk-space check and the size limit.</summary>
    public Func<AudioTrack, long> BitsPerSecond { get; }

    // 16-bit samples at the input's rate and channels (48 kHz stereo when ffprobe does not say).
    private static long Pcm(AudioTrack audio) =>
        (audio.SampleRate > 0 ? audio.SampleRate : 48_000) * (long)(audio.Channels > 0 ? audio.Channels : 2) * 16;
}

/// <summary>
/// <c>Convert to &gt; MP3</c> (and M4A, WAV, FLAC, OGG, Opus) for an audio file, and <c>Extract audio &gt; MP3</c> (and M4A)
/// for a video: writes the main sound of the input as <c>&lt;name&gt;.&lt;ext&gt;</c> beside it, with the file's own tags
/// but none that say where it was recorded. A type is never offered for a file already of that type.
/// </summary>
public sealed class AudioConvert : IOperation
{
    private readonly AudioFormat _format;
    private readonly Ffmpeg _ffmpeg;
    private readonly bool _fromVideo;

    internal AudioConvert(AudioFormat format, Ffmpeg ffmpeg, bool fromVideo = false)
    {
        _format = format;
        _ffmpeg = ffmpeg;
        _fromVideo = fromVideo;
        var name = format.Label.ToLowerInvariant();
        Id = fromVideo ? $"video.extract-{name}" : $"audio.to-{name}";
        MenuPath = fromVideo ? $"Extract audio/{format.Label}" : $"Convert to/{format.Label}";
        Extensions = fromVideo
            ? MediaJob.VideoExtensions
            : [.. MediaJob.AudioExtensions.Where(extension => extension != format.Extension)];
    }

    public string Id { get; }

    public string MenuPath { get; }

    public IReadOnlyCollection<string> Extensions { get; }

    // Sound alone is light work for one core: as many files at once as there are cores (flevert.exe caps it).
    public int MaxParallel => Environment.ProcessorCount;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var info = await _ffmpeg.ProbeAsync(inputPath, ct).ConfigureAwait(false);
        // From a video, say so when other sound tracks are left out (they may be other languages).
        var audio = (_fromVideo ? VideoEncoding.FirstAudio(info) : info.Audio) is [var first, ..]
            ? first
            : throw new MediaException(_fromVideo ? MediaException.NoSoundToExtractMessage : MediaException.NoSoundMessage);
        var estimate = MediaJob.Bytes(info.Duration, _format.BitsPerSecond(audio));
        var desired = MediaJob.Beside(inputPath, _format.Extension);
        MediaJob.RequireSpace(desired, estimate);
        string[] options = ["-map", $"0:{audio.Index}", .. MediaJob.MetadataOptions(info, [audio]), .. _format.Codec(audio)];
        return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _ffmpeg.EncodeAsync(
            inputPath, options, temp, _format.Muxer, info.Duration, MediaJob.MaxOutputBytes(inputPath, estimate), MediaJob.Percent(progress), token),
            ct).ConfigureAwait(false);
    }
}
