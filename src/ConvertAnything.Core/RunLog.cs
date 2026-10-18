using System.Globalization;
using System.Text;

namespace ConvertAnything.Core;

/// <summary>
/// Tab-separated, one line per processed file: time, pid, op-id, input, status, ms, output or error.
/// Logging is best effort: it never throws, because a log failure must not stop or lose a batch.
/// </summary>
public sealed class RunLog(string directory)
{
    private const string CrossProcessLock = @"Local\ConvertAnything.Log";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

    public static string DefaultDirectory { get; } =
        Environment.GetEnvironmentVariable("CONVERTANYTHING_LOG_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertAnything", "logs");

    public string Directory { get; } = directory;

    public string CurrentFile => Path.Combine(Directory, $"ca-{DateTime.Now:yyyyMMdd}.log");

    public void Write(string operationId, FileResult result, TimeSpan elapsed)
    {
        var line = string.Join('\t',
            DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            OneLine(operationId),
            OneLine(result.Input),
            result.Succeeded ? "ok" : "failed",
            ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            OneLine((result.Succeeded ? result.Output : result.Error) ?? ""));

        BestEffort(() =>
        {
            System.IO.Directory.CreateDirectory(Directory);
            // FileMode.Append is not an atomic append across processes, so serialise writers machine-wide per session.
            using var gate = new Mutex(initiallyOwned: false, CrossProcessLock);
            var owned = WaitFor(gate);
            try
            {
                using var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(Encoding.UTF8.GetBytes(line + Environment.NewLine));
            }
            finally
            {
                if (owned)
                    gate.ReleaseMutex();
            }
        });
    }

    /// <summary>Deletes daily logs outside the retention window; the window includes today.</summary>
    public void Prune(TimeSpan retention, DateOnly today) => BestEffort(() =>
    {
        if (!System.IO.Directory.Exists(Directory))
            return;

        var oldestKept = today.AddDays(1 - (int)retention.TotalDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "ca-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["ca-".Length..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldestKept)
                BestEffort(() => File.Delete(file));
        }
    });

    private static bool WaitFor(Mutex gate)
    {
        try
        {
            return gate.WaitOne(LockTimeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

#pragma warning disable CA1031 // Logging must never fail the caller.
    private static void BestEffort(Action action)
    {
        try { action(); }
        catch (Exception) { }
    }
#pragma warning restore CA1031

    private static string OneLine(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString();
    }
}
