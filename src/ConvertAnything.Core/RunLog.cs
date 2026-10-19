using System.Globalization;
using System.Text;

namespace ConvertAnything.Core;

/// <summary>
/// Tab-separated, one line per processed file: time, pid, op-id, input, status, ms, output or error.
/// Logging is best effort: it never throws, because a log failure must not stop or lose a batch.
/// </summary>
public sealed class RunLog(string directory)
{
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
            result.Succeeded ? "ok" : result.Skipped ? "skipped" : "failed",
            ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            OneLine((result.Succeeded ? result.Output : result.Error) ?? ""));

        BestEffort(() =>
        {
            System.IO.Directory.CreateDirectory(Directory);
            AppendExclusively(CurrentFile, Encoding.UTF8.GetBytes(line + Environment.NewLine));
        });
    }

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    // Set once a wait timed out: something keeps the file open (a viewer, a stuck writer), so later lines in this process
    // try once instead of adding the full wait to every file.
    private volatile bool _held;

    // FileMode.Append is not an atomic append across processes. Opening without write sharing serialises writers; the
    // file lives in a folder only this user can open, so no other account can hold it (unlike a named mutex).
    private void AppendExclusively(string path, byte[] bytes)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                _held = false;
                return;
            }
            // Only "another handle has it" is worth waiting for; disk full or a bad path will not clear up in 2 s.
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation && !_held)
            {
                if (waited.Elapsed >= LockTimeout)
                {
                    _held = true;
                    throw;
                }

                Thread.Sleep(Math.Min(5 * attempt, 50)); // another writer holds the file
            }
        }
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
        {
            // Control characters forge lines; bidi overrides (Cf) and U+2028/2029 make names display misleadingly.
            var category = char.GetUnicodeCategory(c);
            builder.Append(category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c);
        }
        return builder.ToString();
    }
}
