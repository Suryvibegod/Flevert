namespace Flevert.Ocr.Tests;

/// <summary>
/// Classes whose tests start tesseract run one after the other: a test that finds "its" tesseract as a child of this
/// process would otherwise pick up one started by a test in another class.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TesseractProcesses
{
    public const string Name = "Tesseract processes";
}
