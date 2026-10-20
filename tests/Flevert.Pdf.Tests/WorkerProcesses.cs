namespace Flevert.Pdf.Tests;

/// <summary>
/// The test classes that start PDF workers (<c>flevert.exe pdf-worker</c>) run one at a time: PdfWorkerTests finds,
/// kills and inspects its worker as a child of this test process, and must never pick another class's.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WorkerProcesses
{
    public const string Name = "PDF worker processes";
}
