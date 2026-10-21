using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Flevert.Core;

/// <summary>
/// The order Nautilus shows file names in: GLib's <c>g_utf8_collate_key_for_filename</c>, which Nautilus sorts with,
/// in the user's collation locale. GLib is called rather than imitated: its order follows the locale's collation rules
/// (in en_US "img_0011" comes before "IMG_0009"), which no simple natural comparison reproduces. GLib is part of every
/// desktop that runs Nautilus and is linked from the system, not shipped. Without it (a server, a container) names fall
/// back to a natural comparison of their own (docs/adr/0010-linux-nautilus-port.md).
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class UnixNaturalOrder
{
    private const string Glib = "libglib-2.0.so.0";
    private const int LcCollate = 3; // glibc

    private static readonly bool HasGlib = TryLoadGlib();

    public static int Compare(string x, string y) => HasGlib ? CompareKeys(x, y) : CompareNaturally(x, y);

    // .NET never calls setlocale, so libc collates in the "C" locale until told otherwise; GLib's keys then would not be
    // Nautilus'. Only the collation category is set, from the environment flevert inherits from Nautilus.
    private static bool TryLoadGlib()
    {
        try
        {
            _ = setlocale(LcCollate, "");
            var probe = g_utf8_collate_key_for_filename("a", -1);
            if (probe == 0)
                return false;
            g_free(probe);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // The keys compare as C strings (strcmp), byte by byte.
    private static int CompareKeys(string x, string y)
    {
        var keyX = g_utf8_collate_key_for_filename(x, -1);
        var keyY = g_utf8_collate_key_for_filename(y, -1);
        try
        {
            return MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)keyX)
                .SequenceCompareTo(MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)keyY));
        }
        finally
        {
            g_free(keyX);
            g_free(keyY);
        }
    }

    /// <summary>Runs of digits compare by value (any length), the text between them in the current culture ignoring case,
    /// and names still equal by their characters.</summary>
    internal static int CompareNaturally(string x, string y)
    {
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var (startX, startY) = (i, j);
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                    j++;
                var byValue = CompareDigits(x.AsSpan(startX, i - startX), y.AsSpan(startY, j - startY));
                if (byValue != 0)
                    return byValue;
                continue;
            }

            var (textX, textY) = (i, j);
            while (i < x.Length && !char.IsAsciiDigit(x[i]))
                i++;
            while (j < y.Length && !char.IsAsciiDigit(y[j]))
                j++;
            var byText = CultureInfo.CurrentCulture.CompareInfo.Compare(
                x.AsSpan(textX, i - textX), y.AsSpan(textY, j - textY), CompareOptions.IgnoreCase);
            if (byText != 0)
                return byText;
        }

        var byLength = (x.Length - i).CompareTo(y.Length - j);
        return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
    }

    // Leading zeros do not change the value; when the values are equal, fewer zeros come first ("a1" before "a01").
    private static int CompareDigits(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        var valueX = x.TrimStart('0');
        var valueY = y.TrimStart('0');
        var byValue = valueX.Length != valueY.Length ? valueX.Length.CompareTo(valueY.Length) : valueX.SequenceCompareTo(valueY);
        return byValue != 0 ? byValue : x.Length.CompareTo(y.Length);
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint setlocale(int category, string locale);

    [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_utf8_collate_key_for_filename(string text, nint length);

    [LibraryImport(Glib)]
    private static partial void g_free(nint memory);
}
