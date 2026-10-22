using System.Globalization;
using System.Runtime.Versioning;

namespace Flevert.Core;

/// <summary>
/// The most memory a running tool has taken, by RLIMIT_DATA's own count (VmData in /proc), looked at every 50 ms until
/// disposed. Linux reports a refused allocation to nobody; a tool that dies after taking most of its allowance was very
/// likely refused more (<see cref="UnixGroup.DiedNearItsLimit"/>).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class UnixMemoryWatch : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    private readonly string _status;
    private readonly Timer _timer;
    private long _peak;

    public UnixMemoryWatch(int processId)
    {
        _status = $"/proc/{processId}/status";
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, Interval);
    }

    /// <summary>The highest VmData seen, in bytes.</summary>
    public long PeakBytes => Interlocked.Read(ref _peak);

    // Stopped as soon as the tool has exited. .NET has reaped it by then, so a sample already running may read the status
    // of a process given the same id meanwhile: at worst one wrong peak, for a tool that has ended anyway.
    public void Dispose() => _timer.Dispose();

    private void Sample()
    {
        try
        {
            foreach (var line in File.ReadLines(_status))
            {
                if (!line.StartsWith("VmData:", StringComparison.Ordinal))
                    continue;
                var kilobytes = long.Parse(line["VmData:".Length..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
                var bytes = kilobytes * 1024;
                for (var peak = PeakBytes; bytes > peak; peak = PeakBytes)
                {
                    if (Interlocked.CompareExchange(ref _peak, bytes, peak) == peak)
                        break;
                }

                return;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            // Exited meanwhile.
        }
    }
}
