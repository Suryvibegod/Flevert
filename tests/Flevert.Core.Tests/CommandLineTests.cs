namespace Flevert.Core.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void Parses_run_with_op_id_and_files()
    {
        var command = CommandLine.Parse(["run", "dev.copy", @"C:\a b.txt", @"C:\ảnh.jpg"]);

        var run = Assert.IsType<RunCommand>(command);
        Assert.Equal("dev.copy", run.OperationId);
        Assert.Equal([@"C:\a b.txt", @"C:\ảnh.jpg"], run.Files);
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("run", "dev.copy")]
    [InlineData("frobnicate")]
    [InlineData("classic-menu")]
    [InlineData("classic-menu", "yes")]
    [InlineData("classic-menu", "on", "off")]
    [InlineData("update-check")]
    [InlineData("update-check", "maybe")]
    [InlineData("update-check", "on", "off")]
    public void Rejects_incomplete_or_unknown_commands(params string[] args)
    {
        Assert.IsType<InvalidCommand>(CommandLine.Parse(args));
    }

    [Fact]
    public void Parses_update_check_on_and_off()
    {
        Assert.Equal(new UpdateCheckCommand(On: true), CommandLine.Parse(["update-check", "on"]));
        Assert.Equal(new UpdateCheckCommand(On: false), CommandLine.Parse(["update-check", "off"]));
    }

    [Fact]
    public void Parses_classic_menu_on_and_off()
    {
        Assert.Equal(new ClassicMenuCommand(On: true), CommandLine.Parse(["classic-menu", "on"]));
        Assert.Equal(new ClassicMenuCommand(On: false), CommandLine.Parse(["classic-menu", "off"]));
    }

    [Fact]
    public void Parses_install_menu_with_dev_flag()
    {
        Assert.Equal(new InstallMenuCommand(IncludeDev: true, AllowInsecureLocation: false), CommandLine.Parse(["install-menu", "--dev"]));
        Assert.Equal(new InstallMenuCommand(IncludeDev: false, AllowInsecureLocation: false), CommandLine.Parse(["install-menu"]));
    }

    [Fact]
    public void Insecure_location_override_is_a_separate_flag_from_dev()
    {
        Assert.Equal(new InstallMenuCommand(IncludeDev: false, AllowInsecureLocation: true),
            CommandLine.Parse(["install-menu", "--allow-insecure-location"]));
        Assert.Equal(new InstallMenuCommand(IncludeDev: true, AllowInsecureLocation: true),
            CommandLine.Parse(["install-menu", "--dev", "--allow-insecure-location"]));
        Assert.Equal(new InstallMenuCommand(IncludeDev: true, AllowInsecureLocation: false),
            CommandLine.Parse(["install-menu", "--dev"]));
    }

    [Fact]
    public void Parses_check_location_with_one_folder()
    {
        Assert.Equal(new CheckLocationCommand(@"C:\Users\a\AppData\Local\Programs\Flevert"),
            CommandLine.Parse(["check-location", @"C:\Users\a\AppData\Local\Programs\Flevert"]));
        Assert.IsType<InvalidCommand>(CommandLine.Parse(["check-location"]));
        Assert.IsType<InvalidCommand>(CommandLine.Parse(["check-location", "a", "b"]));
    }

    [Fact]
    public void Parses_uninstall_menu_and_list_ops()
    {
        Assert.IsType<UninstallMenuCommand>(CommandLine.Parse(["uninstall-menu"]));
        Assert.IsType<ListOpsCommand>(CommandLine.Parse(["list-ops"]));
    }
}
