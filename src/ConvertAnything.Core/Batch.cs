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
    internal static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 20;
    private const int MaxPayloadBytes = 1 << 20;
    private const byte Ack = 1;

    /// <returns>A leader that yields every file to process, or <c>null</c> when a running leader took these files.</returns>
    public static async Task<BatchLeader?> LeadOrJoinAsync(
        string operationId, IReadOnlyList<string> files, TimeSpan idle, CancellationToken ct)
    {
        var name = PipeName(operationId);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(name, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (UnauthorizedAccessException)
            {
                return BatchLeader.Alone(files); // name held by another user: never wait on it
            }
            catch (IOException)
            {
                switch (await TrySendAsync(name, files, ct).ConfigureAwait(false))
                {
                    case SendResult.Delivered: return null;
                    case SendResult.Untrusted: return BatchLeader.Alone(files);
                    default: continue; // the leader closed between our two attempts; try to lead
                }
            }

            return BatchLeader.Start(server, files, idle);
        }

        return BatchLeader.Alone(files);
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

    private enum SendResult { Delivered, Retry, Untrusted }

    private static async Task<SendResult> TrySendAsync(string name, IReadOnlyList<string> files, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (client.ConfigureAwait(false))
        {
            try
            {
                await client.ConnectAsync(IoTimeout, ct).ConfigureAwait(false);
                if (!PipePeer.IsSameExecutable(PipePeer.ServerProcessId(client)))
                    return SendResult.Untrusted;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(IoTimeout);
                await WritePathsAsync(client, files, timeout.Token).ConfigureAwait(false);
                var ack = new byte[1];
                return await client.ReadAsync(ack, timeout.Token).ConfigureAwait(false) == 1 && ack[0] == Ack
                    ? SendResult.Delivered
                    : SendResult.Retry;
            }
            catch (UnauthorizedAccessException)
            {
                return SendResult.Untrusted;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                return SendResult.Retry;
            }
        }
    }

    private static string PipeName(string operationId)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return $"ConvertAnything-{identity.User}-{self.SessionId}-{PipeSecret()}-{operationId}";
    }

    // A per-user random part, readable only by this user, so no other account can predict and squat the pipe name.
    private static string PipeSecret()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertAnything");
        var path = Path.Combine(dir, "pipe.key");
        try
        {
            Directory.CreateDirectory(dir);
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            created.Write(System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));
        }
        catch (IOException) when (File.Exists(path))
        {
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var secret = File.ReadAllText(path).Trim();
                if (secret.Length > 0 || attempt >= 10)
                    return secret.Length > 16 ? secret[..16] : secret;
            }
            catch (IOException) when (attempt < 10)
            {
            }

            Thread.Sleep(10); // another process is still writing the key
        }
    }

    internal static byte AckByte => Ack;
}

public sealed class BatchLeader : IAsyncDisposable
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    private volatile bool _consumerWaiting;
    private int _received;

    private BatchLeader(NamedPipeServerStream? server, IReadOnlyList<string> files, TimeSpan idle)
    {
        foreach (var file in files)
            Enqueue(file);

        if (server is null)
        {
            _queue.Writer.Complete();
            _listener = Task.CompletedTask;
        }
        else
        {
            _listener = Task.Run(() => ListenAsync(server, idle));
        }
    }

    /// <summary>Files queued but not yet handed to the consumer.</summary>
    public int Pending => _queue.Reader.Count;

    internal static BatchLeader Start(NamedPipeServerStream server, IReadOnlyList<string> files, TimeSpan idle) => new(server, files, idle);

    internal static BatchLeader Alone(IReadOnlyList<string> files) => new(null, files, TimeSpan.Zero);

    /// <summary>Yields files as they arrive. Completes once the consumer is idle, nothing is queued and no path arrived for the idle window.</summary>
    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
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

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _listener.ConfigureAwait(false);
        _stop.Dispose();
    }

    private void Enqueue(string file)
    {
        if (file.Length > 0 && Interlocked.Increment(ref _received) <= Batch.MaxFilesPerBatch)
            _queue.Writer.TryWrite(file);
    }

    private async Task ListenAsync(NamedPipeServerStream server, TimeSpan idle)
    {
        var ct = _stop.Token;
        await using (server.ConfigureAwait(false))
        {
            Task? connecting = null;
            try
            {
                while (_received < Batch.MaxFilesPerBatch)
                {
                    connecting ??= server.WaitForConnectionAsync(ct);
                    if (await Task.WhenAny(connecting, Task.Delay(idle, ct)).ConfigureAwait(false) != connecting)
                    {
                        if (_consumerWaiting && _queue.Reader.Count == 0)
                            break;
                        continue; // still working: keep the batch open so late files join it
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
                        // A broken or slow client must not end the batch; its sender retries if it got no ack.
                    }

                    if (server.IsConnected)
                        server.Disconnect();
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

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        if (!PipePeer.IsSameExecutable(PipePeer.ClientProcessId(server)))
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Batch.IoTimeout);

        // Queue before acking: if the ack is lost the sender retries and the runner drops the duplicate; no file is lost.
        foreach (var file in await Batch.ReadPathsAsync(server, timeout.Token).ConfigureAwait(false))
            Enqueue(file);

        await server.WriteAsync(new[] { Batch.AckByte }, timeout.Token).ConfigureAwait(false);
        await server.FlushAsync(timeout.Token).ConfigureAwait(false);
        // Disconnect discards unread data; wait until the sender has read the ack.
        await Task.Run(server.WaitForPipeDrain, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
    }
}
