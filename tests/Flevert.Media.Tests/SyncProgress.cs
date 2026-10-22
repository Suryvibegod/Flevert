using Flevert.Core;

namespace Flevert.Media.Tests;

/// <summary>Reports on the reporting thread, unlike <see cref="Progress{T}"/>, so the test sees each report as it is made.</summary>
internal sealed class SyncProgress(Action<FileProgress> report) : IProgress<FileProgress>
{
    public void Report(FileProgress value) => report(value);
}
