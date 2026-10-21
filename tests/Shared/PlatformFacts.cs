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

/// <summary>
/// A test of the Linux side (libc calls, /proc, Nautilus): skipped elsewhere. Put
/// <c>[SupportedOSPlatform("linux")]</c> beside it, so the platform analyzer still checks the test's body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only";
    }
}

/// <inheritdoc cref="LinuxFactAttribute"/>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only";
    }
}
