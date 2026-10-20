using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// How each library qpdf's release links statically into qpdf30.dll shows its version in the binary. qpdf builds them
    /// with whatever vcpkg holds on the day (build-scripts/run-vcpkg), so a new qpdf can bring other versions.
    /// </summary>
    private static readonly Dictionary<string, Func<string, string>> Linked = new(StringComparer.Ordinal)
    {
        ["zlib"] = version => $" inflate {version} Copyright",
        ["libjpeg-turbo"] = version => $"libjpeg-turbo version {version} ",
        // OPENSSL_VERSION_STR, which the default provider reports as its version.
        ["OpenSSL"] = version => $"\0{version}\0",
    };

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

    [Fact]
    public void Each_library_linked_into_qpdf30_is_noticed_with_the_licence_of_the_version_it_holds()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(InstallerScriptTests.RepositoryRoot(), "scripts", "deps.json")));
        var fetched = manifest.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.TryGetProperty("target", out _))
            .ToDictionary(item => "deps/" + item.GetProperty("target").GetString(), item => item.GetProperty("url").GetString()!, StringComparer.Ordinal);
        var binary = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(Bin, "qpdf30.dll")));
        var noticed = new List<string>();

        foreach (var entry in manifest.RootElement.GetProperty("notices").EnumerateArray())
        {
            var title = entry.GetProperty("title").GetString()!;
            var match = Regex.Match(title, @"^(?<name>.+) (?<version>\d+(\.\d+)+), linked into qpdf30\.dll$");
            if (!match.Success)
                continue;
            var (name, version) = (match.Groups["name"].Value, match.Groups["version"].Value);
            noticed.Add(name);

            Assert.True(Linked.TryGetValue(name, out var marker), $"say in {nameof(Linked)} how {name} shows its version in qpdf30.dll");
            Assert.True(binary.Contains(marker(version), StringComparison.Ordinal), $"qpdf30.dll does not hold {name} {version}: \"{title}\" is out of date");
            // Its own licence files at that version's tag, not copies made for another build of the library (pdfium's).
            Assert.All(entry.GetProperty("licences").EnumerateArray().Select(licence => licence.GetString()!), licence =>
            {
                Assert.True(fetched.TryGetValue(licence, out var url), $"{licence} ({title}) is not a file fetch-deps downloads for it");
                Assert.Contains($"/{name.ToLowerInvariant()}/", url!, StringComparison.Ordinal);
                Assert.Matches($"/(v|{Regex.Escape(name.ToLowerInvariant())}-)?{Regex.Escape(version)}/", url!);
            });
        }

        // Every library linked into qpdf30.dll has an entry.
        Assert.Equal(Linked.Keys.Order(StringComparer.Ordinal), noticed.Order(StringComparer.Ordinal));
    }
}
