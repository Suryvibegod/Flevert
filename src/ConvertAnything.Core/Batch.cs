using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Channels;

namespace ConvertAnything.Core;

/// <summary>
/// Explorer starts one ca.exe per selected file. The first process to create the operation's pipe leads the batch and
/// keeps accepting paths while it processes them; the others hand their paths over and exit. See docs/adr/0002.
/// </summary>
public static class Batch
{
    internal const int MaxFilesPerBatch = 1000;
    internal const byte Accepted = 1;
    internal const byte Full = 2;
    internal static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromSeconds(10);
    private const int MaxPayloadBytes = 1 << 20;

    /// <returns>A leader that yields every file to process, or <c>null</c> when a running leader took these files.</returns>
    public static async Task<BatchLeader?> LeadOrJoinAsync(
        string operationId, IReadOnlyList<string> files, TimeSpan idle, CancellationToken ct)
    {
        string name;
        try
        {
            name = PipeName(operationId);
        }
#pragma warning disable CA1031 // Coordination is an optimisation: if it cannot be set up, process these files alone.
        catch (Exception)
#pragma warning restore CA1031
        {
            return BatchLeader.Alone(files);
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < JoinDeadline)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(name, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (UnauthorizedAccessException)
            {
                return BatchLeader.Alone(files); // name held by another account: never wait on it
            }
            catch (IOException)
            {
                switch (await TrySendAsync(name, files, ct).ConfigureAwait(false))
                {
                    case SendResult.Delivered: return null;
                    case SendResult.Refused: return BatchLeader.Alone(files);
                    default: continue; // the leader closed or was busy; try to lead
                }
            }

            return BatchLeader.Start(server, files, idle, onClosed: () => TryRotateSecret(operationId));
        }

        return BatchLeader.Alone(files);
    }

    internal static string PipeName(string operationId)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();
        return $"ConvertAnything-{identity.User}-{self.SessionId}-{ReadOrCreateSecret(SecretPath(operationId))}-{operationId}";
    }

    /// <summary>
    /// A random part of the pipe name, readable only by this user at medium integrity or above, and replaced whenever a
    /// batch of that operation closes, so a name seen in the pipe namespace cannot be used to squat the next batch.
    /// </summary>
    internal static string ReadOrCreateSecret(string path)
    {
        if (TryReadSecret(path) is { } existing)
            return existing;

        var folder = Directory.CreateDirectory(Path.GetDirectoryName(path)!).FullName;
        SecureDirectory.RestrictToOwnerAndAdmins(folder);
        SecureDirectory.TryApplyNoReadUpLabel(folder);

        // Never overwrite here: many processes start at once, and the first key written must be the one they all use.
        if (File.Exists(path) && TryReadSecret(path) is null)
            File.Delete(path); // corrupt or empty key left by a crash
        WriteSecret(path, overwrite: false);
        return TryReadSecret(path) ?? throw new IOException($"Cannot establish batch key {path}.");
    }

    internal static async Task WritePathsAsync(Stream stream, IReadOnlyList<string> files, CancellationToken ct)
    {
        // Raw UTF-16 keeps every valid NTFS name intact, including lone surrogates that UTF-8 would replace.
        var payload = MemoryMarshal.AsBytes(string.Join('\0', files).AsSpan()).ToArray();
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<string[]> ReadPathsAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length is < 0 or > MaxPayloadBytes || length % 2 != 0)
            throw new IOException($"Batch message of {length} bytes rejected.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return new string(MemoryMarshal.Cast<byte, char>(payload)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private enum SendResult { Delivered, Retry, Refused }

    private static async Task<SendResult> TrySendAsync(string name, IReadOnlyList<string> files, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (client.ConfigureAwait(false))
        {
            try
            {
                await client.ConnectAsync(IoTimeout, ct).ConfigureAwait(false);
                if (!PipePeer.IsTrusted(PipePeer.ServerProcessId(client)))
                    return SendResult.Refused;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(IoTimeout);
                await WritePathsAsync(client, files, timeout.Token).ConfigureAwait(false);
                var reply = new byte[1];
                if (await client.ReadAsync(reply, timeout.Token).ConfigureAwait(false) != 1)
                    return SendResult.Retry;
                return reply[0] switch
                {
                    Accepted => SendResult.Delivered,
                    Full => SendResult.Refused,
                    _ => SendResult.Retry,
                };
            }
            catch (UnauthorizedAccessException)
            {
                return SendResult.Refused;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                return SendResult.Retry;
            }
        }
    }

    private static string SecretPath(string operationId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertAnything", "pipes", operationId + ".key");

    private static void TryRotateSecret(string operationId)
    {
        var path = SecretPath(operationId);
        try
        {
            // Also re-secures folders created before the folder was locked down.
            SecureDirectory.RestrictToOwnerAndAdmins(Path.GetDirectoryName(path)!);
            SecureDirectory.TryApplyNoReadUpLabel(Path.GetDirectoryName(path)!);
            WriteSecret(path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteSecret(string path, bool overwrite)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        try
        {
            File.Move(temp, path, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(path))
        {
            // Another process created the key first; use theirs.
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string? TryReadSecret(string path)
    {
        // Dozens of processes start together on first use; a reader can briefly collide with the writer's rename.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var text = File.ReadAllText(path).Trim();
                return text.Length == 32 && text.All(Uri.IsHexDigit) ? text : null;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(5 * attempt);
            }
        }
    }
}

public sealed class BatchLeader : IAsyncDisposable
{
    // A fast operation can drain the queue before slower-starting processes connect; keep the batch open at least this long.
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromSeconds(1.5);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _age = Stopwatch.StartNew();
    private readonly Task _listener;
    private volatile bool _consumerWaiting;
    private int _disposed;

    private BatchLeader(NamedPipeServerStream? server, IReadOnlyList<string> files, TimeSpan idle, Action? onClosed)
    {
        foreach (var file in files)
            TryEnqueue(file);

        if (server is null)
        {
            _queue.Writer.Complete();
            _listener = Task.CompletedTask;
        }
        else
        {
            _listener = Task.Run(() => ListenAsync(server, idle, onClosed));
        }
    }

    /// <summary>Distinct files queued but not yet handed to the consumer.</summary>
    public int Pending => _queue.Reader.Count;

    internal static BatchLeader Start(NamedPipeServerStream server, IReadOnlyList<string> files, TimeSpan idle, Action onClosed) =>
        new(server, files, idle, onClosed);

    internal static BatchLeader Alone(IReadOnlyList<string> files) => new(null, files, TimeSpan.Zero, null);

    /// <summary>
    /// Yields each distinct file as it arrives. Completes once the consumer is idle, nothing is queued and no path arrived
    /// for the idle window. When the consumer stops early (cancel, error) the leader stops accepting files at once.
    /// </summary>
    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            while (true)
            {
                _consumerWaiting = true;
                if (!await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    yield break;
                _consumerWaiting = false;

                while (_queue.Reader.TryRead(out var file))
                    yield return file;
            }
        }
        finally
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _listener.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Disposal must always complete; the listener already logged nothing worth surfacing.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _stop.Dispose();
    }

    private void TryEnqueue(string file)
    {
        lock (_seen)
        {
            if (file.Length > 0 && _seen.Add(file))
                _queue.Writer.TryWrite(file);
        }
    }

    private bool HasRoomFor(IReadOnlyCollection<string> files)
    {
        lock (_seen)
            return _seen.Count + files.Distinct(StringComparer.Ordinal).Count(f => !_seen.Contains(f)) <= Batch.MaxFilesPerBatch;
    }

    private async Task ListenAsync(NamedPipeServerStream server, TimeSpan idle, Action? onClosed)
    {
        var ct = _stop.Token;
        try
        {
            await using (server.ConfigureAwait(false))
            {
                Task? connecting = null;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        connecting ??= server.WaitForConnectionAsync(ct);
                        if (await Task.WhenAny(connecting, Task.Delay(idle, ct)).ConfigureAwait(false) != connecting)
                        {
                            if (_consumerWaiting && _queue.Reader.Count == 0 && _age.Elapsed >= MinimumLifetime)
                                break;
                            continue; // still working, or too young: keep the batch open so late files join it
                        }

                        var finished = connecting;
                        connecting = null;
                        try
                        {
                            await finished.ConfigureAwait(false);
                            await ServeAsync(server, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
                        {
                            // A broken, slow or untrusted client must not end the batch; a sender without a reply retries.
                        }

                        // Disconnect also resets a pipe a client broke, so the next WaitForConnection works.
                        try { server.Disconnect(); }
                        catch (InvalidOperationException) { }
                        catch (IOException) { }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                finally
                {
                    _queue.Writer.TryComplete();
                }

                if (connecting is not null)
                    await connecting.ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
            }
        }
        finally
        {
            _queue.Writer.TryComplete();
            onClosed?.Invoke();
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        if (!PipePeer.IsTrusted(PipePeer.ClientProcessId(server)))
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Batch.IoTimeout);

        var files = await Batch.ReadPathsAsync(server, timeout.Token).ConfigureAwait(false);
        if (!HasRoomFor(files))
        {
            // Refuse the whole message so its sender processes those files itself; never ack what will not be processed.
            await server.WriteAsync(new[] { Batch.Full }, timeout.Token).ConfigureAwait(false);
            return;
        }

        // Queue before acking: if the reply is lost the sender retries and the duplicate is dropped; no file is lost.
        foreach (var file in files)
            TryEnqueue(file);
        await server.WriteAsync(new[] { Batch.Accepted }, timeout.Token).ConfigureAwait(false);
        await server.FlushAsync(timeout.Token).ConfigureAwait(false);

        // Disconnect discards unread data, so wait until the sender has read the reply and closed its end.
        var eof = new byte[1];
        while (await server.ReadAsync(eof, timeout.Token).ConfigureAwait(false) > 0)
        {
        }
    }
}
