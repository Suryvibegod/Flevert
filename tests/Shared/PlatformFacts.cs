namespace Flevert.Tests;

/// <summary>
/// A test of Windows behaviour (ACLs, junctions, the registry, PowerShell): skipped on Linux. Put
/// <c>[SupportedOSPlatform("windows")]</c> beside it, so the platform analyzer still checks the test's body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
    }
}
