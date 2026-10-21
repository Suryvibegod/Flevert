using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Flevert.Core;
using ImageMagick;
using ImageMagick.Configuration;

namespace Flevert.Image;

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

    /// <summary>Pixels per image (400 MP); also the cap on an animation's frames × canvas before it is expanded.</summary>
    internal const ulong MaxArea = 400_000_000;

    /// <summary>ImageMagick's memory limit for the whole process; past it pixels go to disk. Shared out by MemoryBudget.</summary>
    internal const ulong MemoryLimit = 2UL * 1024 * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static bool _initialized;

    public static string ConfigurationDirectory { get; } = Path.Combine(AppData.Root, AppData.MagickFolder);

    /// <summary>How long a start waits for other flevert.exe processes to finish loading a file it must replace.</summary>
    private static readonly TimeSpan ReplacePatience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Puts ImageMagick's configuration files in place, byte for byte. MagickNET.Initialize(files, path) keeps existing
    /// files, so a stale or tampered policy.xml would otherwise survive. A file that already holds the exact bytes, in a
    /// file of its own only this user, SYSTEM and Administrators can change, is kept: another flevert.exe may be loading
    /// it, and Windows refuses to replace a file ImageMagick has open (T17b). Anything else is replaced atomically, so
    /// processes initialising at the same moment never see a half-written policy.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void WriteConfiguration(string directory, TimeSpan? patience = null)
    {
        var elapsed = Stopwatch.StartNew();
        var files = ConfigurationFiles.Default;
        files.Policy.Data = Policy;
        foreach (var file in new[]
                 {
                     files.Colors, files.Configure, files.Delegates, files.English, files.Locale, files.Log,
                     files.Mime, files.Policy, files.Thresholds, files.Type, files.TypeGhostscript,
                 })
        {
            // The bytes File.WriteAllText wrote before T17b (UTF-8, no byte order mark), so existing files still match.
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(file.Data);
            ConfigurationFile.Ensure(Path.Combine(directory, file.FileName), bytes, elapsed, patience ?? ReplacePatience);
        }
    }

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
                return;

            // Relative only if the user's app-data folder could not be resolved; the folder then depends on the working
            // directory, which is exactly what must not decide the configuration. Checked before anything is created.
            if (!Path.IsPathFullyQualified(ConfigurationDirectory))
                throw new InvalidOperationException("Cannot locate the user's local application data folder.");

            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Securing ImageMagick's configuration folder is not implemented on this OS yet.");

            // ImageMagick reads policy.xml from disk; a folder other accounts can write would let them relax it.
            Directory.CreateDirectory(ConfigurationDirectory);
            SecureDirectory.RestrictToOwnerAndAdmins(ConfigurationDirectory);
            // Also the pixel-cache folder for decoded images; flevert.exe labels the whole app-data folder, other hosts may not.
            SecureDirectory.TryApplyNoReadUpLabel(ConfigurationDirectory);

            // ImageMagick also loads configuration from other folders and applies it after ours. A planted policy.xml can
            // redirect temporary files (decoded pixels) or enable delegates while the coder probes below still pass, so
            // refuse to start if any exists. The working directory is one of those folders (Explorer starts verbs in the
            // clicked file's folder, e.g. Downloads): load with ours as the working directory instead.
            if (ForeignConfiguration(ConfigurationSearchFolders()) is { } foreign)
                throw new InvalidOperationException($"Found ImageMagick configuration at {foreign}; refusing to process images with it.");

            WriteConfiguration(ConfigurationDirectory);
            var workingDirectory = Environment.CurrentDirectory;
            Environment.CurrentDirectory = ConfigurationDirectory;
            try
            {
                MagickNET.Initialize(ConfigurationDirectory);
                MagickNET.SetTempDirectory(ConfigurationDirectory);

                ResourceLimits.Width = 40_000;
                ResourceLimits.Height = 40_000;
                ResourceLimits.Area = MaxArea;
                ResourceLimits.Memory = MemoryLimit;
                ResourceLimits.MaxMemoryRequest = 1UL * 1024 * 1024 * 1024;
                ResourceLimits.Disk = 4UL * 1024 * 1024 * 1024;
                ResourceLimits.ListLength = 1_000;
                ResourceLimits.Time = 120;

                VerifyPolicy();
            }
            finally
            {
                Environment.CurrentDirectory = workingDirectory;
            }

            _initialized = true;
        }
    }

    /// <summary>Where ImageMagick looks for configuration besides the folder given to it and the program folder.</summary>
    internal static IReadOnlyList<string> ConfigurationSearchFolders()
    {
        var folders = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageMagick"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageMagick"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ImageMagick"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ImageMagick"),
        };
        foreach (var variable in new[] { "MAGICK_HOME", "MAGICK_CONFIGURE_PATH" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
                folders.AddRange(value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var variable in new[] { "XDG_CONFIG_HOME", "HOME" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
                folders.Add(variable == "HOME" ? Path.Combine(value, ".config", "ImageMagick") : Path.Combine(value, "ImageMagick"));
        }

        return folders;
    }

    // Every file ImageMagick reads from its configuration folders: besides policy and delegates, a planted colors.xml
    // redefined "white", and type.xml points at font files.
    private static readonly string[] KnownConfigurationFiles =
    [
        "policy.xml", "delegates.xml", "colors.xml", "configure.xml", "english.xml", "locale.xml", "log.xml", "mime.xml",
        "thresholds.xml", "type.xml", "type-ghostscript.xml", "coder.xml", "magic.xml",
    ];

    /// <returns>The first ImageMagick configuration file found in <paramref name="folders"/>, or <c>null</c>.</returns>
    internal static string? ForeignConfiguration(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            // Magick.NET points MAGICK_CONFIGURE_PATH at our folder, and child processes inherit it.
            if (string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), ConfigurationDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var name in KnownConfigurationFiles)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Proves the script coders are refused by the coder policy itself (a refusal for another reason, such as a path rule,
    /// does not count). Together with the refusal of foreign configuration, this makes the module fail closed.
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
            catch (MagickPolicyErrorException ex) when (ex.Message.Contains(format.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                continue; // refused by the coder rule, as it must be
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
