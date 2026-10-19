using ImageMagick;

namespace ConvertAnything.Image;

/// <summary>
/// Shares ImageMagick's one memory limit between files converted at the same time (SPEC-image, "Ngân sách bộ nhớ khi
/// chạy song song"). Over the limit ImageMagick moves pixels to disk, and past its disk limit the file fails, so running
/// files in parallel must never fail a file that one at a time would convert. Shares are granted in arrival order, so a
/// large image waiting is never overtaken for ever by small ones; a share larger than the budget runs alone.
/// </summary>
public sealed class MemoryBudget(ulong capacity)
{
    // Source image, the copy being processed and the encoder's buffers, at 4 bytes per pixel (Q8, four channels).
    private const ulong BytesPerPixel = 4 * 3;

    private static readonly Lazy<MemoryBudget> SharedBudget = new(() =>
    {
        MagickSetup.EnsureInitialized();
        return new MemoryBudget(ResourceLimits.Memory);
    });

    private readonly Lock _gate = new();
    private readonly LinkedList<(ulong Need, TaskCompletionSource<IDisposable> Granted)> _waiting = new();
    private ulong _used;

    /// <summary>The budget every image operation in this process shares: ImageMagick's memory limit.</summary>
    public static MemoryBudget Shared => SharedBudget.Value;

    /// <summary>
    /// The memory converting <paramref name="path"/> needs, from its header only. <see cref="ulong.MaxValue"/> when the
    /// header cannot be read: the file then runs alone, and the operation reports what is wrong with it.
    /// </summary>
    public static ulong EstimateNeed(string path)
    {
        try
        {
            return checked(ImageFile.PixelCount(path) * BytesPerPixel);
        }
        catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException or NotSupportedException or OverflowException)
        {
            return ulong.MaxValue;
        }
    }

    public Task<IDisposable> AcquireAsync(ulong bytes, CancellationToken ct)
    {
        var need = Math.Clamp(bytes, 1, capacity);
        LinkedListNode<(ulong, TaskCompletionSource<IDisposable>)> node;
        lock (_gate)
        {
            if (_waiting.Count == 0 && _used + need <= capacity)
            {
                _used += need;
                return Task.FromResult<IDisposable>(new Lease(this, need));
            }

            node = _waiting.AddLast((need, new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously)));
        }

        var granted = node.Value.Item2;
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
        while (_waiting.First is { } first && _used + first.Value.Need <= capacity)
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
