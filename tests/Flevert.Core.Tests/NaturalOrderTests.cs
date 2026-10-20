namespace Flevert.Core.Tests;

/// <summary>The order a many-to-one operation receives its files in: Explorer's (StrCmpLogicalW), ADR-0007 part 2.</summary>
public sealed class NaturalOrderTests
{
    private static string[] Sorted(params string[] names) => [.. names.Order(NaturalOrder.Comparer)];

    [Fact]
    public void Numbers_inside_names_compare_by_value()
    {
        Assert.Equal(["ảnh 1.jpg", "ảnh 2.jpg", "ảnh 10.jpg"], Sorted("ảnh 10.jpg", "ảnh 2.jpg", "ảnh 1.jpg"));
    }

    [Fact]
    public void Case_is_ignored()
    {
        Assert.Equal(0, NaturalOrder.Compare("Ảnh 2.jpg", "ảnh 2.jpg"));
        Assert.Equal(["A.txt", "b.txt", "C.txt"], Sorted("C.txt", "b.txt", "A.txt"));
    }

    [Fact]
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

    [Fact]
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
        var sorted = NaturalOrder.SortPaths([@"C:\b\x.txt", @"C:\a\y 10.txt", @"C:\a\x.txt", @"C:\b\y 9.txt"]);

        Assert.Equal([@"C:\a\x.txt", @"C:\b\x.txt", @"C:\b\y 9.txt", @"C:\a\y 10.txt"], sorted);
    }

    [Fact]
    public void Paths_that_differ_only_in_case_still_get_a_fixed_order()
    {
        // Possible in a case-sensitive folder; whichever order the batch received them in, the result is the same.
        var one = NaturalOrder.SortPaths([@"C:\d\a.txt", @"C:\d\A.txt"]);
        var other = NaturalOrder.SortPaths([@"C:\d\A.txt", @"C:\d\a.txt"]);

        Assert.Equal(one, other);
    }
}
