namespace Flevert.Media.Tests;

/// <summary>A test of the product as only Windows runs it (flevert.exe, job objects); skipped elsewhere (TestFfmpeg).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "flevert.exe and ExternalTool run on Windows only.";
    }
}
