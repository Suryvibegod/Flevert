using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Channels;

namespace Flevert.Core;

/// <summary>
/// Explorer starts one flevert.exe per selected file. The first process to create the operation's pipe leads the batch and
/// keeps accepting paths while it processes them; the others hand their paths over and exit. See docs/adr/0002.
/// </summary>
public static class Batch
{
    internal const int MaxFilesPerBatch = 1000;
    internal const byte Accepted = 1;
    internal const byte Full = 2;
    internal const byte Refused = 3;
    internal static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromSeconds(10);
    private const int MaxPayloadBytes = 1 << 20;

    /// <returns>A leader that yields every file to process, or <c>null</c> when a running leader took these files.</returns>
    public static async Task<BatchLeader?> LeadOrJoinAsync(
        string operationId, IReadOnlyList<string> files, TimeSpan idle, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        var rotatedAfterRefusal = false;
        while (deadline.Elapsed < JoinDeadline)
        {
            string key, name;
            try
            {
                // Recomputed on every attempt: a leader that just closed has rotated the key.
                key = ReadOrCreateSecret(SecretPath(operationId));
                name = PipeName(operationId, key);
            }
#pragma warning disable CA1031 // Coordination is an optimisation: if it cannot be set up, process these files alone.
            catch (Exception)
#pragma warning restore CA1031
            {
                return BatchLeader.Alone(files);
            }

            NamedPipeServerStream? server = TryCreateServer(name, out var foreignOwner);
            if (server is not null)
                return BatchLeader.Start(() => CreateServer(name), server, files, idle, onClosed: () => TryRotateSecret(operationId, key));

            var result = foreignOwner
                ? SendResult.Refused
                : await TrySendAsync(name, files, Remaining(deadline), ct).ConfigureAwait(false);
            switch (result)
            {
                case SendResult.Delivered:
                    return null;
                case SendResult.Refused when !rotatedAfterRefusal:
                    // Someone else holds or answers this name: move the operation to a fresh name once, then retry.
                    rotatedAfterRefusal = true;
                    TryRotateSecret(operationId, key);
                    continue;
                case SendResult.Refused:
                    return BatchLeader.Alone(files);
                default:
                    continue; // the leader closed or was busy; try to lead
            }
        }

        return BatchLeader.Alone(files);
    }

    internal static string PipeName(string operationId) => PipeName(operationId, ReadOrCreateSecret(SecretPath(operationId)));

    internal static string SecretPath(string operationId) => Path.Combine(AppData.Root, AppData.PipesFolder, operationId + ".key");

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

    private static string PipeName(string operationId, string key)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();
        return $"Flevert-{identity.User}-{self.SessionId}-{key}-{operationId}";
    }

    // Zero-size buffers are load-bearing: a write completes only once the client has read it, so a reply is never left
    // unread when the leader disconnects (Disconnect discards unread data).
    internal static NamedPipeServerStream CreateServer(string name) =>
        new(name, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, inBufferSize: 0, outBufferSize: 0);

    private static NamedPipeServerStream? TryCreateServer(string name, out bool foreignOwner)
    {
        foreignOwner = false;
        try
        {
            return CreateServer(name);
        }
        catch (UnauthorizedAccessException)
        {
            foreignOwner = true; // the name is held by another account: never wait on it
            return null;
        }
        catch (IOException)
        {
            return null; // a leader of ours already exists
        }
    }

    private static TimeSpan Remaining(Stopwatch deadline)
    {
        var left = JoinDeadline - deadline.Elapsed;
        return left < IoTimeout ? (left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1)) : IoTimeout;
    }

    /// <param name="connectTimeout">Bounded by the join deadline. Once connected, the exchange keeps the full
    /// <see cref="IoTimeout"/>: cutting it short could drop an ack for files the leader already queued, and the sender
    /// would then process them a second time.</param>
    private static async Task<SendResult> TrySendAsync(string name, IReadOnlyList<string> files, TimeSpan connectTimeout, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (client.ConfigureAwait(false))
        {
            try
            {
                await client.ConnectAsync(connectTimeout, ct).ConfigureAwait(false);
                if (!PipePeer.IsTrusted(PipePeer.ServerProcessId(client)))
                    return SendResult.Refused;

                using var io = CancellationTokenSource.CreateLinkedTokenSource(ct);
                io.CancelAfter(IoTimeout);
                await WritePathsAsync(client, files, io.Token).ConfigureAwait(false);
                var reply = new byte[1];
                if (await client.ReadAsync(reply, io.Token).ConfigureAwait(false) != 1)
                    return SendResult.Retry;
                return reply[0] switch
                {
                    Accepted => SendResult.Delivered,
                    Full or Refused => SendResult.Refused,
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

    /// <summary>Replaces the key only if it is still the one this batch used, so a newer batch's key is never rotated away.</summary>
    private static void TryRotateSecret(string operationId, string usedKey)
    {
        var path = SecretPath(operationId);
        try
        {
            if (TryReadSecret(path) != usedKey)
                return;

            // Also re-secures folders created before the folder was locked down.
            SecureDirectory.RestrictToOwnerAndAdmins(Path.GetDirectoryName(path)!);
            SecureDirectory.TryApplyNoReadUpLabel(Path.GetDirectoryName(path)!);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    WriteSecret(path, overwrite: true);
                    return;
                }
                // Replacing a file another process has open can fail as a sharing violation or as access denied.
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
                {
                    Thread.Sleep(5 * attempt); // a starting process may be reading the old key
                }
            }
        }
#pragma warning disable CA1031 // Best effort: a key that is not rotated only weakens defence in depth.
        catch (Exception) { }
#pragma warning restore CA1031
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
    private static readonly TimeSpan ReplaceBackoff = TimeSpan.FromMilliseconds(20);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _age = Stopwatch.StartNew();
    private readonly Task _listener;
    private readonly Func<uint, bool> _isTrusted;
    private bool _busy;
    private int _running;
    private int _disposed;

    private BatchLeader(
        Func<NamedPipeServerStream>? createServer, NamedPipeServerStream? server, IReadOnlyList<string> files, TimeSpan idle,
        Action? onClosed, Func<uint, bool>? isTrusted = null)
    {
        _isTrusted = isTrusted ?? PipePeer.IsTrusted;
        foreach (var file in files)
            TryEnqueue(file);

        if (server is null || createServer is null)
        {
            _queue.Writer.Complete();
            _listener = Task.CompletedTask;
        }
        else
        {
            _listener = Task.Run(() => ListenAsync(createServer, server, idle, onClosed));
        }
    }

    /// <summary>Distinct files queued but not yet handed to the consumer.</summary>
    public int Pending => _queue.Reader.Count;

    /// <summary>
    /// How many files the consumer is still converting. A consumer that runs several files at once asks for the next one
    /// while earlier ones run; the batch stays open until this is back to zero. Report it before asking for the next file.
    /// </summary>
    public void ReportRunning(int count) => Volatile.Write(ref _running, count);

    /// <param name="createServer">Creates a replacement pipe instance with the same name, after a client broke the current one.</param>
    internal static BatchLeader Start(
        Func<NamedPipeServerStream> createServer, NamedPipeServerStream server, IReadOnlyList<string> files, TimeSpan idle, Action onClosed) =>
        new(createServer, server, files, idle, onClosed);

    internal static BatchLeader Start(NamedPipeServerStream server, IReadOnlyList<string> files, TimeSpan idle, Action onClosed) =>
        new(() => throw new IOException("No replacement pipe available."), server, files, idle, onClosed);

    /// <param name="isTrusted">Tests only: replaces the peer check, so a test can play an untrusted client.</param>
    internal static BatchLeader Start(
        Func<NamedPipeServerStream> createServer, NamedPipeServerStream server, IReadOnlyList<string> files, TimeSpan idle,
        Action onClosed, Func<uint, bool> isTrusted) =>
        new(createServer, server, files, idle, onClosed, isTrusted);

    internal static BatchLeader Alone(IReadOnlyList<string> files) => new(null, null, files, TimeSpan.Zero, null);

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
                Volatile.Write(ref _busy, false);
                if (!await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    yield break;

                while (true)
                {
                    // Marked busy before taking a file, so the listener never sees "nothing queued and idle" while one is held.
                    Volatile.Write(ref _busy, true);
                    if (!_queue.Reader.TryRead(out var file))
                        break;
                    yield return file;
                }
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
#pragma warning disable CA1031 // Disposal must always complete.
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

    // Read the queue before the flag: the consumer sets the flag before taking, so a held file is always visible in one of
    // them. A parallel consumer reports a taken file as running before it asks for the next, so it stays visible after.
    private bool IsIdle => _queue.Reader.Count == 0 && !Volatile.Read(ref _busy) && Volatile.Read(ref _running) == 0;

    private async Task ListenAsync(Func<NamedPipeServerStream> createServer, NamedPipeServerStream server, TimeSpan idle, Action? onClosed)
    {
        var ct = _stop.Token;
        // Restarted only when a trusted sender delivered paths, measured from the end of that exchange: connections that
        // carry nothing (a vanished, silent or untrusted client) must not keep the batch open.
        var sinceActivity = Stopwatch.StartNew();
        var nameLost = false;
        Task? connecting = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Checked after every connection too, so clients that keep connecting cannot hold the batch open.
                if (sinceActivity.Elapsed >= idle && _age.Elapsed >= MinimumLifetime && IsIdle)
                    break;

                connecting ??= server.WaitForConnectionAsync(ct);
                var wait = idle - sinceActivity.Elapsed;
                if (wait < ReplaceBackoff)
                    wait = IsIdle ? ReplaceBackoff : idle; // while a long conversion runs, check once per window

                if (await Task.WhenAny(connecting, Task.Delay(wait, ct)).ConfigureAwait(false) != connecting)
                    continue; // still working, too young, or recently active: keep the batch open so late files join it

                var finished = connecting;
                connecting = null;
                if (finished.IsFaulted || finished.IsCanceled)
                {
                    _ = finished.Exception; // observed
                    if (ct.IsCancellationRequested)
                        break;
                    // A client vanished before the connection completed; the instance cannot be trusted to recover.
                    if (await TryReplaceAsync(server, createServer).ConfigureAwait(false) is not { } replacement)
                    {
                        nameLost = true;
                        break;
                    }

                    server = replacement;
                    await Task.Delay(ReplaceBackoff, ct).ConfigureAwait(false);
                    continue;
                }

                var delivered = false;
                try
                {
                    delivered = await ServeAsync(server, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // A broken, slow or untrusted client must not end the batch; a sender without a reply retries.
                }

                try
                {
                    server.Disconnect();
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    if (await TryReplaceAsync(server, createServer).ConfigureAwait(false) is not { } replacement)
                    {
                        nameLost = true;
                        break;
                    }

                    server = replacement;
                }

                if (delivered)
                    sinceActivity.Restart();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            // Rotate before the name is released, so nobody can claim the old name in between. Whatever it throws, the
            // queue must complete, or the consumer would wait for ever. If the name was lost, whoever holds it now may be
            // a newer batch using the same key: rotating would send its followers elsewhere.
            try
            {
                if (!nameLost)
                    onClosed?.Invoke();
            }
#pragma warning disable CA1031 // Closing must always finish; rotation is best effort.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            _queue.Writer.TryComplete();
            await server.DisposeAsync().ConfigureAwait(false);
            if (connecting is not null)
                await connecting.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    /// <returns>A new instance, or <c>null</c> when the name was taken while it was free (another process or account).</returns>
    private static async Task<NamedPipeServerStream?> TryReplaceAsync(NamedPipeServerStream server, Func<NamedPipeServerStream> createServer)
    {
        await server.DisposeAsync().ConfigureAwait(false);
        try
        {
            return createServer();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <returns>Whether a trusted sender delivered a message (accepted or refused as full).</returns>
    private async Task<bool> ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Batch.IoTimeout);

        if (!_isTrusted(PipePeer.ClientProcessId(server)))
        {
            // Read (and drop) the request first: senders write before they read, and on this unbuffered pipe a reply
            // written now would wait for a reader that is itself waiting for its own write to be read.
            _ = await Batch.ReadPathsAsync(server, timeout.Token).ConfigureAwait(false);
            await server.WriteAsync(new[] { Batch.Refused }, timeout.Token).ConfigureAwait(false);
            return false;
        }

        var files = await Batch.ReadPathsAsync(server, timeout.Token).ConfigureAwait(false);
        if (!HasRoomFor(files))
        {
            // Refuse the whole message so its sender processes those files itself; never ack what will not be processed.
            await server.WriteAsync(new[] { Batch.Full }, timeout.Token).ConfigureAwait(false);
            return true;
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

        return true;
    }
}
