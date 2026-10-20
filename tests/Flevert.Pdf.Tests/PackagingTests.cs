namespace Flevert.Pdf.Tests;

public class PackagingTests
{
    [Fact]
    public void Pdfium_is_copied_next_to_the_assemblies_that_use_it() =>
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "pdfium.dll")), "pdfium.dll is not in the output folder");
}
