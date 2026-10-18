using System.Globalization;

namespace ConvertAnything.Core;

/// <summary>Tab-separated, one line per processed file: time, pid, op-id, input, status, ms, output or error.</summary>
public sealed class RunLog(string directory)
{
    private static readonly object Gate = new();

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertAnything", "logs");

    public string Directory { get; } = directory;

    public string CurrentFile => Path.Combine(Directory, $"ca-{DateTime.Now:yyyyMMdd}.log");

    public void Write(string operationId, FileResult result, TimeSpan elapsed)
    {
        var detail = result.Succeeded ? result.Output : OneLine(result.Error!);
        var line = string.Join('\t',
            DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            operationId,
            result.Input,
            result.Succeeded ? "ok" : "failed",
            ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            detail);

        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            AppendShared(CurrentFile, line + Environment.NewLine);
        }
    }

    // Two operations can run in separate processes at once; share the file and retry rather than fail the batch.
    private static void AppendShared(string path, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20 * attempt);
            }
            catch (IOException)
            {
                return;
            }
        }
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" | ").Replace('\t', ' ');
}
