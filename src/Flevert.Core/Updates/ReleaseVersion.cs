using System.Globalization;
using System.Text.RegularExpressions;

namespace Flevert.Core.Updates;

/// <summary>A published release's version, as named by its tag (vMAJOR.MINOR.PATCH).</summary>
public readonly partial record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// <summary>The version of this build, from &lt;Version&gt; in Directory.Build.props.</summary>
    public static ReleaseVersion Current { get; } = FromAssembly();

    /// <summary>
    /// Only tags the release workflow accepts: ASCII digits (.NET's \d also matches other scripts' digits), at most five
    /// per part so the numbers cannot overflow, nothing before or after.
    /// </summary>
    public static bool TryParseTag(string? tag, out ReleaseVersion version)
    {
        version = default;
        if (tag is null)
            return false;
        var match = TagPattern().Match(tag);
        if (!match.Success)
            return false;
        version = new ReleaseVersion(Part(match, 1), Part(match, 2), Part(match, 3));
        return true;
    }

    public int CompareTo(ReleaseVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static int Part(Match match, int group) => int.Parse(match.Groups[group].ValueSpan, CultureInfo.InvariantCulture);

    private static ReleaseVersion FromAssembly()
    {
        var version = typeof(ReleaseVersion).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new ReleaseVersion(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    // \z, not $: $ also matches before a final newline.
    [GeneratedRegex(@"^v([0-9]{1,5})\.([0-9]{1,5})\.([0-9]{1,5})\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
