using Flevert.Core;
using ImageMagick;

namespace Flevert.Image;

/// <summary>
/// Shares ImageMagick's one memory limit between files converted at the same time (docs/specs/image.md, "Ngân sách bộ nhớ khi
/// chạy song song"). Over the limit ImageMagick moves pixels to disk, and past its disk limit the file fails, so running
/// files in parallel must never fail a file that one at a time would convert. Shares are granted in arrival order, so a
/// large image waiting is never overtaken for ever by small ones; a share over half the budget runs alone.
/// </summary>
public sealed class MemoryBudget
{
    // The source image, the copy being processed and the encoder's buffers.
    private const ulong Copies = 3;

    // Decoders read the whole file into memory and copy its profiles and comments, outside ImageMagick's accounting.
    private const ulong FileCopies = 2;

    private readonly ulong _capacity;
    private readonly Lock _gate = new();
    private readonly LinkedList<(ulong Need, TaskCompletionSource<IDisposable> Granted)> _waiting = new();
    private ulong _used;

    public MemoryBudget(ulong capacity)
    {
        ArgumentOutOfRangeException.ThrowIfZero(capacity);
        _capacity = capacity;
    }

    /// <summary>
    /// The budget every image operation in this process shares: ImageMagick's memory limit, or a quarter of the memory
    /// this machine has if that is less. Built without touching ImageMagick, so a failed set-up fails one file, not all.
    /// </summary>
    public static MemoryBudget Shared { get; } = new(Math.Max(1, Math.Min(MagickSetup.MemoryLimit, (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4)));

    /// <summary>The most this budget grants at once.</summary>
    public ulong Capacity => _capacity;

    /// <summary>
    /// How much of the budget a request for <paramref name="bytes"/> takes: more than half is all of it (see
    /// <see cref="AcquireAsync"/>). One that holds shares already can tell from it that one more would never be granted.
    /// </summary>
    public ulong ShareFor(ulong bytes) => bytes > _capacity / 2 ? _capacity : Math.Max(bytes, 1);

    /// <summary>
    /// The memory converting <paramref name="path"/> needs, from its header and size only. <see cref="ulong.MaxValue"/>
    /// when the header cannot be read: the file then runs alone, and the operation reports what is wrong with it.
    /// </summary>
    public static ulong EstimateNeed(string path)
    {
        try
        {
            return Math.Max(checked(ImageFile.DecodedBytes(path) * Copies), FileNeed(path));
        }
        catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException or NotSupportedException or OverflowException)
        {
            return ulong.MaxValue;
        }
    }

    /// <summary>What reading <paramref name="path"/> takes before any pixel is decoded: decoders load the whole file.</summary>
    public static ulong FileNeed(string path)
    {
        try
        {
            return checked((ulong)PinnedInput.Length(path) * FileCopies);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OverflowException)
        {
            return ulong.MaxValue;
        }
    }

    /// <summary>
    /// Waits for <paramref name="bytes"/> of the budget, granted in arrival order (the order estimates finish, which is
    /// close to the batch order). More than half the budget is taken as all of it: ImageMagick's time limit runs per
    /// image on the clock, and a large image slowed down by others beside it could pass that limit.
    /// </summary>
    public Task<IDisposable> AcquireAsync(ulong bytes, CancellationToken ct)
    {
        var need = ShareFor(bytes);
        LinkedListNode<(ulong Need, TaskCompletionSource<IDisposable> Granted)> node;
        lock (_gate)
        {
            if (_waiting.Count == 0 && need <= _capacity - _used)
            {
                _used += need;
                return Task.FromResult<IDisposable>(new Lease(this, need));
            }

            node = _waiting.AddLast((need, new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously)));
        }

        var granted = node.Value.Granted;
        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(() =>
            {
                lock (_gate)
                {
                    if (node.List is null)
                        return; // already granted: the caller owns the lease and disposes it
                    _waiting.Remove(node);
                    granted.TrySetCanceled(ct);
                    GrantWaiting(); // the ones behind it may fit now
                }
            });
            _ = granted.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return granted.Task;
    }

    private void Release(ulong need)
    {
        lock (_gate)
        {
            _used -= need;
            GrantWaiting();
        }
    }

    // Called under the lock. Strictly in order: stops at the first waiter that does not fit.
    private void GrantWaiting()
    {
        while (_waiting.First is { } first && first.Value.Need <= _capacity - _used)
        {
            _waiting.RemoveFirst();
            _used += first.Value.Need;
            if (!first.Value.Granted.TrySetResult(new Lease(this, first.Value.Need)))
                _used -= first.Value.Need;
        }
    }

    private sealed class Lease(MemoryBudget budget, ulong need) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                budget.Release(need);
        }
    }
}
