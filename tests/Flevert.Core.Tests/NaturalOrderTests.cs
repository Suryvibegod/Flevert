namespace Flevert.Core.Tests;

/// <summary>
/// The order a many-to-one operation receives its files in: the file manager's, Explorer's (StrCmpLogicalW) on Windows and
/// Nautilus' (GLib's g_utf8_collate_key_for_filename) on Linux. ADR-0007 part 2.
/// </summary>
public sealed class NaturalOrderTests
{
    private static string[] Sorted(params string[] names) => [.. names.Order(NaturalOrder.Comparer)];

    [Fact]
    public void Numbers_inside_names_compare_by_value()
    {
        Assert.Equal(["ảnh 1.jpg", "ảnh 2.jpg", "ảnh 10.jpg"], Sorted("ảnh 10.jpg", "ảnh 2.jpg", "ảnh 1.jpg"));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Case_is_ignored()
    {
        Assert.Equal(0, NaturalOrder.Compare("Ảnh 2.jpg", "ảnh 2.jpg"));
        Assert.Equal(["A.txt", "b.txt", "C.txt"], Sorted("C.txt", "b.txt", "A.txt"));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Vietnamese_letters_sort_beside_their_base_letter_and_numbers_still_count()
    {
        // A tone or a stroke ranks below the letters and the numbers, so "ảnh" stays with "anh" (before "b"), the number
        // decides between them, and "đ" falls between "d" and "e".
        Assert.Equal(["Ảnh 2.jpg", "anh 3.jpg", "ảnh 10.jpg", "b 1.jpg"], Sorted("b 1.jpg", "ảnh 10.jpg", "anh 3.jpg", "Ảnh 2.jpg"));
        Assert.Equal(["d.txt", "đ.txt", "e.txt"], Sorted("e.txt", "đ.txt", "d.txt"));
        Assert.Equal(["Đà Lạt 2.jpg", "Đà Lạt 10.jpg"], Sorted("Đà Lạt 10.jpg", "Đà Lạt 2.jpg"));
    }

    [Fact]
    public void Names_made_only_of_digits_sort_by_value()
    {
        Assert.Equal(["1", "9", "10", "100"], Sorted("100", "10", "9", "1"));
    }

    [Fact]
    public void Digit_runs_up_to_18_digits_compare_by_value()
    {
        Assert.Equal(["a 999999999999999999", "a 1000000000000000000"], Sorted("a 1000000000000000000", "a 999999999999999999"));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Longer_digit_runs_give_a_consistent_order_like_explorer_does()
    {
        // Past 64 bits StrCmpLogicalW, and so Explorer, falls back to comparing the digits as text. What matters is that
        // the order is Explorer's and is consistent: no exception, antisymmetric, the same every time.
        string[] names = ["a 100000000000000000000000000000", "a 99999999999999999999999999999", "a 2", "a 18446744073709551616"];

        var once = Sorted(names);

        Assert.Equal("a 2", once[0]);
        Assert.Equal(once, Sorted([.. names.Reverse()]));
        foreach (var x in names)
        {
            foreach (var y in names)
                Assert.Equal(Math.Sign(NaturalOrder.Compare(x, y)), -Math.Sign(NaturalOrder.Compare(y, x)));
        }
    }

    [Fact]
    public void Paths_sort_by_file_name_then_by_folder()
    {
        var sorted = NaturalOrder.SortPaths(["C:/b/x.txt", "C:/a/y 10.txt", "C:/a/x.txt", "C:/b/y 9.txt"]);

        Assert.Equal(["C:/a/x.txt", "C:/b/x.txt", "C:/b/y 9.txt", "C:/a/y 10.txt"], sorted);
    }

    [Fact]
    public void Paths_that_differ_only_in_case_still_get_a_fixed_order()
    {
        // Possible in a case-sensitive folder; whichever order the batch received them in, the result is the same.
        var one = NaturalOrder.SortPaths(["C:/d/a.txt", "C:/d/A.txt"]);
        var other = NaturalOrder.SortPaths(["C:/d/A.txt", "C:/d/a.txt"]);

        Assert.Equal(one, other);
    }
}

/// <summary>On Linux the order is Nautilus' own: GLib's collation of file names, in the user's locale.</summary>
public sealed class UnixNaturalOrderTests
{
    private const string Python = "/usr/bin/python3";

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void Names_come_in_the_order_nautilus_shows_them()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux only");
        string[] names =
        [
            "ảnh 10.jpg", "ảnh 2.jpg", "Ảnh 1.jpg", "ảnh 1.jpg", "a.jpg", "a1.jpg", "a01.jpg", "a001.jpg", "b.jpg", "B.jpg",
            "_x.jpg", "Đà Lạt.jpg", "Da Lat.jpg", "da lat 2.jpg", "IMG_0010.HEIC", "IMG_0009.HEIC", "img_0011.heic",
            "report.pdf", "report (1).pdf", "report 1.pdf", "report-1.pdf", "report.1.pdf", "z.jpg", "Z.jpg", "10.jpg",
            "9.jpg", "1.10.jpg", "1.9.jpg", "ảnh 18446744073709551616.jpg", "ảnh 18446744073709551615.jpg",
        ];
        var nautilus = SortedByGlib(names);

        var ours = names.Reverse().Order(NaturalOrder.Comparer).ToArray();

        Assert.Equal(nautilus, ours);
    }

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void Numbers_inside_names_compare_by_value_whatever_their_length()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux only");

        Assert.True(NaturalOrder.Compare("ảnh 2.jpg", "ảnh 10.jpg") < 0);
        Assert.True(NaturalOrder.Compare("a 18446744073709551615", "a 18446744073709551616") < 0);
        Assert.True(NaturalOrder.Compare("a 99999999999999999999999999999", "a 100000000000000000000000000000") < 0);
    }

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void Without_glib_numbers_still_compare_by_value_and_case_is_ignored()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux only");
        string[] names = ["ảnh 10", "b2", "ảnh 2", "a01", "B", "a1"];

        var sorted = names.Order(Comparer<string>.Create(UnixNaturalOrder.CompareNaturally)).ToArray();

        Assert.Equal(["a1", "a01", "ảnh 2", "ảnh 10", "B", "b2"], sorted);
    }

    // GLib's order in the locale the tests run in, the one flevert inherits from Nautilus.
    private static string[] SortedByGlib(string[] names)
    {
        Skip.IfNot(File.Exists(Python), "no system Python to ask GLib");
        var start = new System.Diagnostics.ProcessStartInfo(Python)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in new[]
                 {
                     "-I", "-c",
                     "import json, locale, sys\n" +
                     "locale.setlocale(locale.LC_ALL, '')\n" +
                     "from gi.repository import GLib\n" +
                     "names = json.load(sys.stdin)\n" +
                     "json.dump(sorted(names, key=lambda n: GLib.utf8_collate_key_for_filename(n, -1)), sys.stdout)\n",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var python = System.Diagnostics.Process.Start(start)!;
        python.StandardInput.Write(System.Text.Json.JsonSerializer.Serialize(names));
        python.StandardInput.Close();
        var output = python.StandardOutput.ReadToEnd();
        var error = python.StandardError.ReadToEnd();
        python.WaitForExit();
        Skip.If(python.ExitCode != 0 && error.Contains("gi", StringComparison.Ordinal), "PyGObject is not installed");
        Assert.True(python.ExitCode == 0, error);
        return System.Text.Json.JsonSerializer.Deserialize<string[]>(output)!;
    }
}
