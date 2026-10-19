namespace Flevert.Core.Tests;

/// <summary>Runs batch tests alone: they raise the thread-pool minimum and spawn processes, which skews timing elsewhere.</summary>
[CollectionDefinition(nameof(SequentialBatchTests), DisableParallelization = true)]
public sealed class SequentialBatchTests;
