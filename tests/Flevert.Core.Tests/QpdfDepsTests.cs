namespace Flevert.Core.Tests;

/// <summary>
/// What fetch-deps.ps1 keeps of qpdf's release is copied next to flevert.exe and shipped. Only the DLLs qpdf.exe and
/// qpdf30.dll import are kept (their import tables, read on 2026-10-20, name msvcp140, vcruntime140 and vcruntime140_1;
/// nothing loads the other five runtime DLLs of the zip), and those must be Microsoft's own, unmodified.
/// </summary>
public sealed class QpdfDepsTests
{
    private static readonly string Bin = Path.Combine(InstallerScriptTests.RepositoryRoot(), @"deps\qpdf\bin");

    private static readonly string[] Kept = ["msvcp140.dll", "qpdf.exe", "qpdf30.dll", "vcruntime140.dll", "vcruntime140_1.dll"];

    [Fact]
    public void Only_qpdf_and_the_runtime_dlls_it_imports_are_kept()
    {
        Assert.True(Directory.Exists(Bin), "deps\\ is missing: run scripts\\fetch-deps.ps1");

        var files = Directory.GetFiles(Bin).Select(Path.GetFileName).Select(name => name!.ToLowerInvariant()).Order(StringComparer.Ordinal);

        Assert.Equal(Kept, files);
    }

    [Fact]
    public void The_runtime_dlls_kept_beside_qpdf_carry_a_valid_microsoft_signature()
    {
        var run = PowerShellScript.Command(
            $"Get-ChildItem '{Bin}' -Filter *.dll | Where-Object Name -ne 'qpdf30.dll' | ForEach-Object {{ $s = Get-AuthenticodeSignature $_.FullName; " +
            "'{0}|{1}|{2}' -f $_.Name, $s.Status, $s.SignerCertificate.Subject }");

        Assert.True(run.ExitCode == 0, run.Output);
        var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line =>
        {
            var parts = line.Split('|');
            Assert.True(parts[1] == "Valid" && parts[2].Contains("O=Microsoft Corporation,", StringComparison.Ordinal), line);
        });
    }
}
