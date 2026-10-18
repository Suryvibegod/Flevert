using System.Globalization;

namespace ConvertAnything.Core;

/// <summary>Tab-separated, one line per processed file: time, op-id, input, status, ms, output or error.</summary>
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
            operationId,
            result.Input,
            result.Succeeded ? "ok" : "failed",
            ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            detail);

        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(CurrentFile, line + Environment.NewLine);
        }
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" | ").Replace('\t', ' ');
}
