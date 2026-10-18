using System.IO.Pipes;
using System.Text;

namespace ConvertAnything.Core;

/// <summary>
/// Explorer starts one ca.exe per selected file. The first process to create the operation's pipe leads the batch;
/// the others hand their paths over and exit. The leader stops listening once no path arrives for the idle window.
/// </summary>
public static class Batch
{
    private const int MaxAttempts = 20;
    private const byte Ack = 1;
    private const int MaxPayloadBytes = 1 << 20;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <returns>Every file the leader must process, or <c>null</c> when another process took these files.</returns>
    public static async Task<IReadOnlyList<string>?> CollectAsync(
        string operationId, IReadOnlyList<string> files, TimeSpan idle, CancellationToken ct)
    {
        var name = PipeName(operationId);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryCreateServer(name) is { } server)
                return await LeadAsync(server, files, idle, ct).ConfigureAwait(false);

            if (await TrySendAsync(name, files, ct).ConfigureAwait(false))
                return null;
        }

        // A pipe we can neither own nor talk to (e.g. squatted by another user): process alone rather than lose files.
        return files;
    }

    private static string PipeName(string operationId)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return $"ConvertAnything-{Environment.UserName}-{self.SessionId}-{operationId}";
    }

    private static NamedPipeServerStream? TryCreateServer(string name)
    {
        try
        {
            return new NamedPipeServerStream(name, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> LeadAsync(
        NamedPipeServerStream server, IReadOnlyList<string> files, TimeSpan idle, CancellationToken ct)
    {
        var all = new List<string>(files);
        await using (server.ConfigureAwait(false))
        {
            while (true)
            {
                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(idle);
                try
                {
                    await server.WaitForConnectionAsync(window.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    all.AddRange(await ReadPathsAsync(server, ct).ConfigureAwait(false));
                    await server.WriteAsync(new[] { Ack }, ct).ConfigureAwait(false);
                    await server.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The sender disconnected before the ack; it will retry, so its paths are not counted here.
                }

                server.Disconnect();
            }
        }

        return all;
    }

    private static async Task<bool> TrySendAsync(string name, IReadOnlyList<string> files, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (client.ConfigureAwait(false))
        {
            try
            {
                await client.ConnectAsync(ConnectTimeout, ct).ConfigureAwait(false);
                await WritePathsAsync(client, files, ct).ConfigureAwait(false);
                var ack = new byte[1];
                return await client.ReadAsync(ack, ct).ConfigureAwait(false) == 1 && ack[0] == Ack;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static async Task WritePathsAsync(Stream stream, IReadOnlyList<string> files, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\n', files));
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string[]> ReadPathsAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length is < 0 or > MaxPayloadBytes)
            throw new IOException($"Batch message of {length} bytes rejected.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload).Split('\n');
    }
}
