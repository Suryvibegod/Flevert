using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ImageMagick;
using Flevert.Core;

namespace Flevert.Image.Tests;

public sealed class MagickSetupTests : IDisposable
{
    private readonly TempDir _dir = new();

    public MagickSetupTests()
    {
        MagickSetup.EnsureInitialized();
        // As the real configuration folder: files created here can be changed only by this user, SYSTEM and Administrators.
        SecureDirectory.RestrictToOwnerAndAdmins(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    // Each input below is read without complaint by a default Magick.NET (checked by hand), so these tests fail if the
    // limits or the explicit-format read are removed.

    [Fact]
    public void Image_wider_than_the_limit_is_rejected_quickly()
    {
        // A fixture: the limit applies to the whole process, so the test cannot create this image itself.
        var wide = Path.Combine(AppContext.BaseDirectory, "fixtures", "wide-50000x1.png");
        var timer = Stopwatch.StartNew();

        Assert.ThrowsAny<MagickException>(() => ImageFile.Read(wide));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"took {timer.Elapsed}");
    }

    [Fact]
    public void A_jpg_holding_a_script_is_not_handed_to_any_other_decoder()
    {
        // ImageMagick is never asked to guess; only our own signature check picks another decoder, and only from the
        // allow-list. MVG text matches none, so it is decoded as JPEG and fails.
        var disguised = _dir.File("photo.jpg", "push graphic-context\nviewbox 0 0 8 8\npop graphic-context\n");

        Assert.ThrowsAny<MagickException>(() => ImageFile.Read(disguised));
    }

    // Only HEIF and AVIF brands go to libheif; an MP4 or MOV renamed to ".png" is decoded as what its name says (and fails).
    [Theory]
    [InlineData("heic", MagickFormat.Heic)]
    [InlineData("mif1", MagickFormat.Heic)]
    [InlineData("avif", MagickFormat.Avif)]
    [InlineData("isom", MagickFormat.Png)]
    [InlineData("qt  ", MagickFormat.Png)]
    public void Only_heif_and_avif_brands_are_recognised_in_an_iso_media_file(string brand, MagickFormat expected)
    {
        var path = Path.Combine(_dir.Path, "clip.png");
        WriteIsoMedia(path, brand);

        Assert.Equal(expected, ImageFile.RealFormat(path));
    }

    // The "avif" brand is unambiguous; only generic brands (mif1) leave the choice to the extension.
    [Theory]
    [InlineData("photo.heic", "avif", MagickFormat.Avif)]
    [InlineData("photo.avif", "heic", MagickFormat.Heic)]
    [InlineData("photo.avif", "mif1", MagickFormat.Avif)]
    [InlineData("photo.heic", "mif1", MagickFormat.Heic)]
    public void A_specific_brand_wins_over_the_extension_within_the_heif_family(string name, string brand, MagickFormat expected)
    {
        var path = Path.Combine(_dir.Path, name);
        WriteIsoMedia(path, brand);

        Assert.Equal(expected, ImageFile.RealFormat(path));
    }

    private static void WriteIsoMedia(string path, string brand)
    {
        var bytes = new byte[32];
        "\0\0\0\u0018ftyp"u8.CopyTo(bytes);
        System.Text.Encoding.ASCII.GetBytes(brand).CopyTo(bytes, 8);
        File.WriteAllBytes(path, bytes);
    }

    [Theory]
    [InlineData("photo.jpg", MagickFormat.Png)]
    [InlineData("photo.png", MagickFormat.WebP)]
    [InlineData("photo.jpg", MagickFormat.Gif)]
    public void A_file_named_with_the_wrong_extension_is_read_as_what_it_is(string name, MagickFormat actual)
    {
        // Chat apps and browsers often save WebP or PNG images as ".jpg".
        var path = Path.Combine(_dir.Path, name);
        using (var image = new MagickImage(MagickColors.Red, 8, 8))
            image.Write(path, actual);

        using var images = ImageFile.Read(path);

        Assert.Equal(actual, images[0].Format);
        Assert.Equal(8u, images[0].Width);
    }

    // TEXT, URL and EPHEMERAL are allowed by the default policy; MVG and MSL are listed for completeness.
    [Theory]
    [InlineData("mvg")]
    [InlineData("msl")]
    [InlineData("text")]
    [InlineData("url")]
    [InlineData("ephemeral")]
    public void Dangerous_coders_are_blocked_by_policy_even_when_named_explicitly(string coder)
    {
        var script = _dir.File($"x.{coder}", "push graphic-context\npop graphic-context\n");

        Assert.ThrowsAny<MagickException>(() => new MagickImage($"{coder}:{script}"));
    }

    // ImageMagick also reads policy.xml and delegates.xml from MAGICK_HOME, %LOCALAPPDATA%\ImageMagick, the XDG and HOME
    // config folders; a planted one can redirect temporary files or enable delegates without touching the three probes.
    [Theory]
    [InlineData("policy.xml")]
    [InlineData("delegates.xml")]
    [InlineData("colors.xml")] // a planted one redefined "white" (round-3 review)
    [InlineData("type.xml")]
    public void Configuration_planted_in_an_imagemagick_search_folder_is_found(string name)
    {
        var planted = Path.Combine(_dir.Path, "ImageMagick");
        Directory.CreateDirectory(planted);
        var file = Path.Combine(planted, name);
        File.WriteAllText(file, "<policymap />");

        Assert.Equal(file, MagickSetup.ForeignConfiguration([_dir.Combine("empty"), planted]));
        Assert.Null(MagickSetup.ForeignConfiguration([_dir.Combine("empty")]));
    }

    [Fact]
    public void The_folders_searched_for_foreign_configuration_include_the_user_ones()
    {
        var folders = MagickSetup.ConfigurationSearchFolders();

        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageMagick"), folders);
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageMagick"), folders);
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ImageMagick"), folders);
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ImageMagick"), folders);
    }

    [Fact]
    public void The_loaded_policy_is_verified_to_refuse_script_coders()
    {
        // ImageMagick also loads a policy.xml from the working directory, after ours; a permissive one there would
        // re-enable MVG/MSL/TEXT. Initialisation must prove the coders are refused, and fail closed otherwise.
        MagickSetup.VerifyPolicy();
    }

    [Fact]
    public void The_folder_for_policy_and_decoded_image_caches_is_labelled_no_read_up()
    {
        // Pixel caches of decoded images live here; a low-integrity process of the same user must not read them.
        // Reading the label through .NET needs SeSecurityPrivilege; icacls shows it without.
        using var icacls = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{MagickSetup.ConfigurationDirectory}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        var output = icacls.StandardOutput.ReadToEnd();
        icacls.WaitForExit();

        Assert.Contains("Mandatory Level", output, StringComparison.Ordinal);
        Assert.Contains("NR", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_or_tampered_policy_on_disk_is_replaced_before_loading()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "policy.xml"), "<policymap></policymap>");

        MagickSetup.WriteConfiguration(_dir.Path);

        var policy = File.ReadAllText(Path.Combine(_dir.Path, "policy.xml"));
        Assert.Contains("""<policy domain="coder" rights="none" pattern="*" />""", policy, StringComparison.Ordinal);
    }

    // ImageMagick opens its configuration files for reading without delete sharing (the C runtime's default), so while
    // one flevert.exe initialises, another cannot replace them. Before T17b every start replaced them and failed with
    // "UnauthorizedAccessException: Access to the path is denied" when two image operations started together.
    private static FileStream OpenLikeImageMagick(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    [Fact]
    public void A_policy_that_is_already_right_is_kept_while_another_process_reads_it()
    {
        MagickSetup.WriteConfiguration(_dir.Path);
        var policy = Path.Combine(_dir.Path, "policy.xml");
        var expected = File.ReadAllBytes(policy);

        using (OpenLikeImageMagick(policy))
            MagickSetup.WriteConfiguration(_dir.Path);

        Assert.Equal(expected, File.ReadAllBytes(policy));
    }

    [Fact]
    public async Task A_stale_policy_another_process_is_reading_is_replaced_once_it_lets_go()
    {
        var policy = Path.Combine(_dir.Path, "policy.xml");
        File.WriteAllText(policy, "<policymap></policymap>");
        var reader = OpenLikeImageMagick(policy);
        var release = Task.Delay(300).ContinueWith(_ => reader.Dispose(), TaskScheduler.Default);

        try
        {
            MagickSetup.WriteConfiguration(_dir.Path);
        }
        finally
        {
            await release;
        }

        Assert.Contains("""<policy domain="coder" rights="none" pattern="*" />""", File.ReadAllText(policy), StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_policy_that_cannot_be_replaced_is_an_error_and_leaves_no_temporary_file()
    {
        var policy = Path.Combine(_dir.Path, "policy.xml");
        File.WriteAllText(policy, "<policymap></policymap>");

        using (OpenLikeImageMagick(policy))
            Assert.ThrowsAny<IOException>(() => MagickSetup.WriteConfiguration(_dir.Path, TimeSpan.FromMilliseconds(200)));

        Assert.Equal("<policymap></policymap>", File.ReadAllText(policy));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public void A_right_policy_another_account_may_change_is_replaced()
    {
        MagickSetup.WriteConfiguration(_dir.Path);
        var policy = new FileInfo(Path.Combine(_dir.Path, "policy.xml"));
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var acl = policy.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
        policy.SetAccessControl(acl);

        MagickSetup.WriteConfiguration(_dir.Path);

        var rules = new FileInfo(policy.FullName).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(users));
    }

    [Fact]
    public void A_right_policy_reached_through_a_hard_link_is_replaced_by_a_file_of_its_own()
    {
        // The link's target keeps its own permissions, which other accounts may be able to change; only a file created
        // in the protected folder is trusted, even when its bytes are right today.
        var right = _dir.Combine("right");
        Directory.CreateDirectory(right);
        MagickSetup.WriteConfiguration(right);
        var outside = _dir.Combine("outside.xml");
        File.Copy(Path.Combine(right, "policy.xml"), outside);
        var config = _dir.Combine("config");
        Directory.CreateDirectory(config);
        MagickSetup.WriteConfiguration(config);
        var policy = Path.Combine(config, "policy.xml");
        File.Delete(policy);
        var hardLink = new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{policy}\" \"{outside}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using (var mklink = Process.Start(hardLink)!)
        {
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        MagickSetup.WriteConfiguration(config);
        File.WriteAllText(outside, "<policymap></policymap>");

        Assert.Equal(File.ReadAllBytes(Path.Combine(right, "policy.xml")), File.ReadAllBytes(policy));
    }

    [Fact]
    public void Policy_and_temporary_files_live_in_a_folder_only_this_user_and_admins_can_open()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var allowed = new[] { me.User!.Value, "S-1-5-18", "S-1-5-32-544" };

        var rules = new DirectoryInfo(MagickSetup.ConfigurationDirectory).GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>();

        Assert.All(rules, r => Assert.Contains(r.IdentityReference.Value, allowed));
    }
}
