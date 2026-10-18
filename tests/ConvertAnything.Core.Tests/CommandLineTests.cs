namespace ConvertAnything.Core.Tests;

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
    public void Rejects_incomplete_or_unknown_commands(params string[] args)
    {
        Assert.IsType<InvalidCommand>(CommandLine.Parse(args));
    }

    [Fact]
    public void Parses_install_menu_with_dev_flag()
    {
        Assert.Equal(new InstallMenuCommand(IncludeDev: true), CommandLine.Parse(["install-menu", "--dev"]));
        Assert.Equal(new InstallMenuCommand(IncludeDev: false), CommandLine.Parse(["install-menu"]));
    }

    [Fact]
    public void Parses_uninstall_menu_and_list_ops()
    {
        Assert.IsType<UninstallMenuCommand>(CommandLine.Parse(["uninstall-menu"]));
        Assert.IsType<ListOpsCommand>(CommandLine.Parse(["list-ops"]));
    }
}
