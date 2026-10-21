using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Flevert.Core;

/// <summary>
/// Starts every tool on Linux from one thread that lives as long as flevert. A tool is set to die with flevert (setpriv
/// --pdeathsig, <see cref="UnixGroup"/>), and Linux sends that signal when the <em>thread</em> that started the process
/// ends, not the process: a tool started from a thread-pool thread, which can retire while the tool runs, would be killed
/// with it.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class UnixStarter
{
    private static readonly BlockingCollection<(ProcessStartInfo Start, TaskCompletionSource<Process> Started)> Requests = [];
    private static readonly Lazy<Thread> Starter = new(() =>
    {
        // A background thread: it never keeps flevert running, and it ends only when flevert does.
        var thread = new Thread(Serve) { IsBackground = true, Name = "Flevert tool starter" };
        thread.Start();
        return thread;
    });

    /// <summary>Starts <paramref name="start"/> on the starter thread and returns the process once it has started.</summary>
    public static Process Start(ProcessStartInfo start)
    {
        _ = Starter.Value;
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Add((start, started));
        return started.Task.GetAwaiter().GetResult();
    }

    private static void Serve()
    {
        foreach (var (start, started) in Requests.GetConsumingEnumerable())
        {
            try
            {
                started.SetResult(Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} did not start."));
            }
#pragma warning disable CA1031 // Handed to the caller, who sees it as Process.Start's own.
            catch (Exception e)
#pragma warning restore CA1031
            {
                started.SetException(e);
            }
        }
    }
}
