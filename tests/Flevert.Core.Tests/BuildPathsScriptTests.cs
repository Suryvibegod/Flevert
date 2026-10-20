using System.Text;

namespace Flevert.Core.Tests;

/// <summary>scripts/test-build-paths.ps1, which CI runs on what it built (docs/specs/pdf.md, v0.2.1, P7).</summary>
public sealed class BuildPathsScriptTests : IDisposable
{
    private const string BuildPath = @"D:\a\Flevert\Flevert";
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    public void An_assembly_naming_the_build_folder_fails_and_is_named(string encoding)
    {
        var path = BuildPath + @"\src\Flevert.Core\obj\Release\net10.0-windows\Flevert.Core.pdb";
        Directory.CreateDirectory(_dir.Combine("bin"));
        File.WriteAllBytes(_dir.Combine(@"bin\Flevert.Core.dll"), [.. "MZ\0\0"u8, .. (encoding == "utf-8" ? Encoding.UTF8 : Encoding.Unicode).GetBytes(path), 0]);

        var (exitCode, output) = PowerShellScript.Run("test-build-paths.ps1", ["-Folder", _dir.Path, "-BuildPath", BuildPath]);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Flevert.Core.dll", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Assemblies_with_the_mapped_path_pass()
    {
        File.WriteAllBytes(_dir.Combine("flevert.dll"), [.. "MZ\0\0/_/src/Flevert.Cli/obj/Release/net10.0-windows/flevert.pdb\0"u8]);
        // Not Flevert's: another build machine's path in a library is none of this check's business.
        File.WriteAllBytes(_dir.Combine("pdfium.dll"), Encoding.UTF8.GetBytes(BuildPath));

        var (exitCode, output) = PowerShellScript.Run("test-build-paths.ps1", ["-Folder", _dir.Path, "-BuildPath", BuildPath]);

        Assert.True(exitCode == 0, output);
        Assert.Contains("No build path in 1 assemblies.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_without_flevert_assemblies_fails_rather_than_passing_on_nothing()
    {
        var (exitCode, _) = PowerShellScript.Run("test-build-paths.ps1", ["-Folder", _dir.Path, "-BuildPath", BuildPath]);

        Assert.NotEqual(0, exitCode);
    }
}
