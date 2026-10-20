namespace Flevert.Pdf.Tests;

public class PackagingTests
{
    [Fact]
    public void Pdfium_is_copied_next_to_the_assemblies_that_use_it() =>
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "pdfium.dll")), "pdfium.dll is not in the output folder");

    [Theory]
    [InlineData("qpdf.exe")]
    [InlineData("qpdf30.dll")]
    [InlineData("msvcp140.dll")]
    [InlineData("vcruntime140.dll")]
    [InlineData("vcruntime140_1.dll")]
    public void Qpdf_and_the_runtime_it_imports_are_copied_next_to_the_assemblies_that_use_it(string file) =>
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, file)), $"{file} is not in the output folder");
}
