namespace Flevert.Ocr.Tests;

/// <summary>A test of the product as only Windows runs it (tesseract.exe, job objects); skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "flevert.exe and ExternalTool run on Windows only.";
    }
}
