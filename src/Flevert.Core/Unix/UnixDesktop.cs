using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Flevert.Core;

/// <summary>
/// The progress window of a slow batch on Linux: <c>zenity --progress</c>, a process of its own that reads the bar's
/// percentage and its text on standard input and exits 1 when Cancel is pressed (docs/specs/linux.md). It dies with
/// flevert, like the tools (<see cref="UnixGroup"/>).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ZenityProgress : IDisposable
{
    public const string Zenity = "/usr/bin/zenity";

    private readonly Process _zenity;
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly Task _watching;
    private bool _closing;

    private ZenityProgress(Process zenity)
    {
        _zenity = zenity;
        _watching = WatchAsync();
    }

    /// <summary>Completes when the user closed the window or pressed Cancel; never when flevert closed it.</summary>
    public Task Cancelled => _cancelled.Task;

    /// <returns>The window, or <c>null</c> when it cannot be shown (no zenity, no display): the batch runs without one.</returns>
    public static ZenityProgress? Start(string operation, string zenity = Zenity)
    {
        if (!File.Exists(zenity))
            return null;
        try
        {
            var start = new ProcessStartInfo(UnixGroup.Setsid) { UseShellExecute = false, RedirectStandardInput = true };
            foreach (var argument in UnixGroup.Prefix(zenity, limits: null))
                start.ArgumentList.Add(argument);
            foreach (var argument in new[] { "--progress", $"--title=Flevert: {operation}", "--text=Starting…", "--percentage=0", "--width=420" })
                start.ArgumentList.Add(argument);
            start.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return new ZenityProgress(UnixStarter.Start(start));
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The bar and the text of the Windows progress window (<see cref="BatchProgressText"/>).</summary>
    public void Report(BatchProgress progress, string? lastStarted)
    {
        var (value, maximum) = BatchProgressText.Bar(progress);
        var percent = maximum == 0 ? 0 : Math.Clamp(value * 100 / maximum, 0, 99); // 100 would close the window early
        var text = BatchProgressText.Describe(progress, lastStarted);
        if (BatchProgressText.DescribeWithin(progress) is { } within)
            text += " — " + within;
        lock (_gate)
        {
            if (_closing)
                return;
            try
            {
                var input = _zenity.StandardInput;
                input.Write(string.Create(CultureInfo.InvariantCulture, $"{percent}\n# {Markup.Escape(OneLine(text))}\n"));
                input.Flush();
            }
            catch (IOException)
            {
                // The window is gone; WatchAsync says whether it was a cancel.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _closing = true;
        try
        {
            _zenity.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        // The watch sees the exit (and that flevert closed it) before the process is let go.
        _ = _watching.Wait(TimeSpan.FromSeconds(2));
        _zenity.Dispose();
    }

    private async Task WatchAsync()
    {
        await _zenity.WaitForExitAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (!_closing)
                _cancelled.TrySetResult();
        }
    }

    // A file name can hold a line break, which zenity would read as a new command.
    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}

/// <summary>
/// The notification at the end of a batch on Linux: org.freedesktop.Notifications, through <c>gdbus</c>, which GNOME,
/// KDE and the other desktops serve. Without a desktop session it fails, and the batch has its log.
/// </summary>
[SupportedOSPlatform("linux")]
public static class DesktopNotification
{
    public const string Gdbus = "/usr/bin/gdbus";
    private const int LifetimeMs = 6000;

    /// <summary>Shows <paramref name="summary"/>. Never throws.</summary>
    public static async Task ShowAsync(BatchSummary summary, string gdbus = Gdbus)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!File.Exists(gdbus))
            return;
        // Each argument is GVariant text, typed so gdbus never has to guess; the body is markup on most desktops.
        string[] arguments =
        [
            "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications",
            "--method", "org.freedesktop.Notifications.Notify",
            GVariantString("Flevert"), "uint32 0", GVariantString(summary.HasFailures ? "dialog-warning" : "image-x-generic"),
            GVariantString("Flevert"), GVariantString(Markup.Escape(summary.Text)), "@as []", "@a{sv} {}",
            string.Create(CultureInfo.InvariantCulture, $"int32 {LifetimeMs}"),
        ];
        try
        {
            await ExternalTool.RunAsync(gdbus, arguments, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No session bus, no notification server: nothing more to do than the log already did.
        }
    }

    /// <summary>A GVariant text string: single quotes, with backslashes, quotes and control characters escaped.</summary>
    internal static string GVariantString(string text)
    {
        var builder = new StringBuilder("'");
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '\\' => @"\\",
                '\'' => @"\'",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ when char.IsControl(c) => string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"),
                _ => c.ToString(),
            });
        }

        return builder.Append('\'').ToString();
    }
}

/// <summary>Pango markup, which zenity and notification servers read: the text shows as it is.</summary>
internal static class Markup
{
    public static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>
/// A batch run from the Nautilus menu: the progress window for a batch still running after two seconds, Cancel stopping
/// it, and one notification at the end, as the Windows app's BatchUi does.
/// </summary>
[SupportedOSPlatform("linux")]
public static class UnixBatchUi
{
    private static readonly TimeSpan ShowProgressAfter = TimeSpan.FromSeconds(2);

    /// <param name="operation">What the window's title names (the menu path, as on Windows).</param>
    /// <param name="batch">Runs the batch with the window's progress and Cancel.</param>
    /// <returns>Whether every file was processed and none failed.</returns>
    /// <param name="question">What the operation asks in a dialog (a password, a watermark's text): the window waits while
    /// it is open, then as long again as for a batch without questions, and it hears the batch's progress.</param>
    public static bool Run(
        string operation, RunLog log, Func<IProgress<BatchProgress>?, CancellationToken, Task<BatchOutcome>> batch,
        string zenity = ZenityProgress.Zenity, string gdbus = DesktopNotification.Gdbus, IUnixQuestion? question = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(batch);
        using var cancel = new CancellationTokenSource();
        var progress = new WindowProgress(question);
        var work = Task.Run(async () =>
        {
            try
            {
                return await batch(progress, cancel.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Whatever happens, the batch must end with a notification and the process must exit.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                log.Write(operation, new FileResult("(batch)", null, ex.ToString()), TimeSpan.Zero);
                return new BatchOutcome([new FileResult("(batch)", null, "batch failed")], 1);
            }
        });

        ZenityProgress? window = null;
        var quiet = Stopwatch.StartNew();
        while (!work.Wait(TimeSpan.FromMilliseconds(100)))
        {
            if (question?.Asking == true)
            {
                quiet.Restart();
                continue;
            }

            if (quiet.Elapsed < ShowProgressAfter)
                continue;
            window = ZenityProgress.Start(operation, zenity);
            if (window is not null)
            {
                progress.Show(window);
                _ = window.Cancelled.ContinueWith(_ => cancel.Cancel(), TaskScheduler.Default);
            }

            break;
        }

        var outcome = work.GetAwaiter().GetResult();
        window?.Dispose();
        DesktopNotification.ShowAsync(BatchSummary.Describe(outcome, log.Directory), gdbus).GetAwaiter().GetResult();
        return outcome.NoFailures;
    }

    // Keeps the latest report, so a window shown late starts from where the batch is.
    private sealed class WindowProgress(IUnixQuestion? question) : IProgress<BatchProgress>
    {
        private readonly Lock _gate = new();
        private BatchProgress? _last;
        private string? _lastStarted;
        private ZenityProgress? _window;

        public void Report(BatchProgress value)
        {
            lock (_gate)
            {
                if (value.Current is { } current)
                    _lastStarted = current;
                _last = value;
                _window?.Report(value, _lastStarted);
                question?.Report(value);
            }
        }

        public void Show(ZenityProgress window)
        {
            lock (_gate)
            {
                _window = window;
                if (_last is { } last)
                    window.Report(last, _lastStarted);
            }
        }
    }
}

/// <summary>
/// A question a batch asks in a window on Linux (a password, a watermark's text): the batch's progress tells it how many
/// files an answer could be for, and the progress window waits while it is open.
/// </summary>
public interface IUnixQuestion
{
    /// <summary>A dialog of this question is open.</summary>
    bool Asking { get; }

    /// <summary>The batch's latest progress.</summary>
    void Report(BatchProgress progress);
}

/// <summary>What a zenity dialog printed, held in a buffer of its own that <see cref="Dispose"/> wipes (it can be a
/// password).</summary>
public sealed class ZenityReply(int exitCode, byte[] buffer, int length) : IDisposable
{
    public int ExitCode { get; } = exitCode;

    /// <summary>The bytes zenity wrote on standard output.</summary>
    public ReadOnlySpan<byte> Output => buffer.AsSpan(0, length);

    public void Dispose() => System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
}

/// <summary>Runs one zenity dialog, which dies with flevert, and reads its answer without ever making it a string.</summary>
[SupportedOSPlatform("linux")]
public static class ZenityDialog
{
    // Far more than a dialog of Flevert's prints: two passwords of 127 bytes, a watermark of a few hundred.
    private const int MaxOutput = 64 * 1024;

    /// <exception cref="OperationCanceledException">The batch was cancelled; the dialog was closed.</exception>
    /// <exception cref="InvalidOperationException">zenity is missing: there is no way to ask.</exception>
    public static async Task<ZenityReply> AskAsync(IEnumerable<string> arguments, CancellationToken ct, string zenity = ZenityProgress.Zenity)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!File.Exists(zenity))
            throw new InvalidOperationException($"{zenity} is missing: Flevert asks in its dialogs.");
        var start = new ProcessStartInfo(UnixGroup.Setsid) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in UnixGroup.Prefix(zenity, limits: null).Concat(arguments))
            start.ArgumentList.Add(argument);

        using var dialog = UnixStarter.Start(start);
        using var close = ct.Register(() =>
        {
            try
            {
                dialog.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        });
        var buffer = new byte[MaxOutput];
        var length = 0;
        var output = dialog.StandardOutput.BaseStream;
        int read;
        while (length < buffer.Length && (read = await output.ReadAsync(buffer.AsMemory(length), CancellationToken.None).ConfigureAwait(false)) > 0)
            length += read;
        await dialog.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var reply = new ZenityReply(dialog.ExitCode, buffer, length);
        if (ct.IsCancellationRequested)
        {
            reply.Dispose();
            ct.ThrowIfCancellationRequested();
        }

        return reply;
    }
}
