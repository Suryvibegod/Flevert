using ConvertAnything.Core;
using ImageMagick;
using ImageMagick.Configuration;

namespace ConvertAnything.Image;

/// <summary>
/// One-time ImageMagick configuration: an allow-list security policy and resource limits, applied before any image is
/// read. Untrusted images are the main attack surface of this module (decompression bombs, script coders like MVG/MSL
/// that read other files, indirect "@file" reads).
/// </summary>
public static class MagickSetup
{
    // Last matching rule wins: deny everything first, then allow only the formats this module reads or writes.
    // PDF is write-only here (images to PDF); reading PDF would need Ghostscript, which is not shipped.
    // XC only creates a solid-colour canvas in memory and reads no file.
    private const string Policy = """
        <policymap>
          <policy domain="delegate" rights="none" pattern="*" />
          <policy domain="filter" rights="none" pattern="*" />
          <policy domain="coder" rights="none" pattern="*" />
          <policy domain="coder" rights="read | write" pattern="{JPEG,JPG,PNG,PNG8,PNG24,PNG32,WEBP,AVIF,GIF,BMP,BMP3,TIFF,TIF}" />
          <policy domain="coder" rights="read" pattern="{HEIC,HEIF}" />
          <policy domain="coder" rights="read" pattern="XC" />
          <policy domain="coder" rights="write" pattern="PDF" />
          <policy domain="path" rights="none" pattern="@*" />
          <policy domain="path" rights="none" pattern="fd:*" />
        </policymap>
        """;

    private static readonly Lock Gate = new();
    private static bool _initialized;

    public static string ConfigurationDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertAnything", "magick");

    /// <summary>
    /// Writes ImageMagick's configuration files, always replacing what is there. MagickNET.Initialize(files, path) keeps
    /// existing files, so a stale or tampered policy.xml would otherwise survive. Each file is replaced atomically, so
    /// processes initialising at the same moment never see a half-written policy.
    /// </summary>
    internal static void WriteConfiguration(string directory)
    {
        var files = ConfigurationFiles.Default;
        files.Policy.Data = Policy;
        foreach (var file in new[]
                 {
                     files.Colors, files.Configure, files.Delegates, files.English, files.Locale, files.Log,
                     files.Mime, files.Policy, files.Thresholds, files.Type, files.TypeGhostscript,
                 })
        {
            var target = Path.Combine(directory, file.FileName);
            var temp = $"{target}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, file.Data);
            File.Move(temp, target, overwrite: true);
        }
    }

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
                return;

            // ImageMagick reads policy.xml from disk; a folder other accounts can write would let them relax it.
            Directory.CreateDirectory(ConfigurationDirectory);
            SecureDirectory.RestrictToOwnerAndAdmins(ConfigurationDirectory);
            // Also the pixel-cache folder for decoded images; ca.exe labels the whole app-data folder, other hosts may not.
            SecureDirectory.TryApplyNoReadUpLabel(ConfigurationDirectory);

            WriteConfiguration(ConfigurationDirectory);
            MagickNET.Initialize(ConfigurationDirectory);
            MagickNET.SetTempDirectory(ConfigurationDirectory);

            ResourceLimits.Width = 40_000;
            ResourceLimits.Height = 40_000;
            ResourceLimits.Area = 400_000_000;
            ResourceLimits.Memory = 2UL * 1024 * 1024 * 1024;
            ResourceLimits.MaxMemoryRequest = 1UL * 1024 * 1024 * 1024;
            ResourceLimits.Disk = 4UL * 1024 * 1024 * 1024;
            ResourceLimits.ListLength = 1_000;
            ResourceLimits.Time = 120;

            VerifyPolicy();
            _initialized = true;
        }
    }

    /// <summary>
    /// Proves the script coders are refused. ImageMagick also loads a <c>policy.xml</c> from the working directory and
    /// applies it after ours, so a permissive file planted there (for example in Downloads, where Explorer starts verbs)
    /// would re-enable them. ca.exe moves its working directory first; this check makes the module fail closed anyway.
    /// </summary>
    internal static void VerifyPolicy()
    {
        foreach (var format in new[] { MagickFormat.Mvg, MagickFormat.Msl, MagickFormat.Text })
        {
            try
            {
                using var image = new MagickImage();
                image.Read("viewbox 0 0 1 1\n"u8.ToArray(), new MagickReadSettings { Format = format, Width = 1, Height = 1 });
            }
            catch (MagickPolicyErrorException)
            {
                continue; // refused, as it must be
            }
#pragma warning disable CA1031 // Any other outcome means the coder ran; never continue with that policy.
            catch (Exception ex)
            {
                throw new InvalidOperationException($"ImageMagick's {format} coder is not blocked by policy; refusing to process images.", ex);
            }
#pragma warning restore CA1031

            throw new InvalidOperationException($"ImageMagick's {format} coder is not blocked by policy; refusing to process images.");
        }
    }
}
