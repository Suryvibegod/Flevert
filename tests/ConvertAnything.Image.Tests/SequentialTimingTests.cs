namespace ConvertAnything.Image.Tests;

/// <summary>
/// Runs timing-sensitive tests alone: other classes encoding in parallel made an operation's own decode several times
/// slower than the decode the test measured just before, so a cancel meant for the encode landed in the decode.
/// </summary>
[CollectionDefinition(nameof(SequentialTimingTests), DisableParallelization = true)]
public sealed class SequentialTimingTests;
