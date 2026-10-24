namespace Flevert.Pdf.Tests;

public class PackagingTests
{
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Pdfium_is_copied_next_to_the_assemblies_that_use_it() =>
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "pdfium.dll")), "pdfium.dll is not in the output folder");

    [WindowsTheory]
    [SupportedOSPlatform("windows")]
    [InlineData("qpdf.exe")]
    [InlineData("qpdf30.dll")]
    [InlineData("msvcp140.dll")]
    [InlineData("vcruntime140.dll")]
    [InlineData("vcruntime140_1.dll")]
    public void Qpdf_and_the_runtime_it_imports_are_copied_next_to_the_assemblies_that_use_it(string file) =>
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, file)), $"{file} is not in the output folder");

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Libpdfium_is_copied_next_to_the_assemblies_that_use_it_on_linux() =>
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "libpdfium.so")), "libpdfium.so is not in the output folder");

    // qpdf keeps its release's bin/ and lib/: it finds libqpdf through $ORIGIN/../lib (scripts/deps-linux.json).
    [LinuxTheory]
    [SupportedOSPlatform("linux")]
    [InlineData("qpdf/bin/qpdf")]
    [InlineData("qpdf/lib/libqpdf.so.30")]
    [InlineData("qpdf/lib/libjpeg.so.8")]
    public void Qpdf_and_the_libraries_it_ships_with_are_copied_below_the_assemblies_that_use_it_on_linux(string file) =>
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, file)), $"{file} is not in the output folder");

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Qpdf_stays_executable_once_copied_on_linux() =>
        Assert.True((File.GetUnixFileMode(Path.Combine(AppContext.BaseDirectory, "qpdf", "bin", "qpdf")) & UnixFileMode.UserExecute) != 0,
            "qpdf lost its execute permission on the way to the output folder");
}
