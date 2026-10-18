using System.Diagnostics;
using ImageMagick;

namespace ConvertAnything.Image.Tests;

public sealed class MagickSetupTests : IDisposable
{
    private readonly TempDir _dir = new();

    public MagickSetupTests() => MagickSetup.EnsureInitialized();

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
    public void Jpg_whose_content_is_another_format_is_not_decoded_as_that_format()
    {
        var disguised = Path.Combine(_dir.Path, "photo.jpg");
        using (var png = new MagickImage(MagickColors.Red, 8, 8))
            png.Write(disguised, MagickFormat.Png);

        Assert.ThrowsAny<MagickException>(() => ImageFile.Read(disguised));
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

    [Fact]
    public void A_stale_or_tampered_policy_on_disk_is_replaced_before_loading()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "policy.xml"), "<policymap></policymap>");

        MagickSetup.WriteConfiguration(_dir.Path);

        var policy = File.ReadAllText(Path.Combine(_dir.Path, "policy.xml"));
        Assert.Contains("""<policy domain="coder" rights="none" pattern="*" />""", policy, StringComparison.Ordinal);
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
