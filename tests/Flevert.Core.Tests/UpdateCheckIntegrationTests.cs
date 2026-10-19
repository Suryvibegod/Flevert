using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Flevert.Core.Updates;

namespace Flevert.Core.Tests;

/// <summary>
/// The real flevert.exe, with its UI, asks for new versions after a batch: the wiring from the command line through the
/// batch window to the checker, which the unit tests cannot see. The fake GitHub answers with the running version, so no
/// question comes up and the process ends on its own.
/// </summary>
[Collection(nameof(SequentialBatchTests))]
public sealed class UpdateCheckIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();
    private readonly byte[]? _savedState;

    public UpdateCheckIntegrationTests()
    {
        // The check keeps its state in the real %LOCALAPPDATA%\Flevert\update; put back whatever was there.
        _savedState = File.Exists(UpdateStateStore.DefaultPath) ? File.ReadAllBytes(UpdateStateStore.DefaultPath) : null;
        if (_savedState is not null)
            File.Delete(UpdateStateStore.DefaultPath);
    }

    public void Dispose()
    {
        if (_savedState is not null)
            File.WriteAllBytes(UpdateStateStore.DefaultPath, _savedState);
        else if (File.Exists(UpdateStateStore.DefaultPath))
            File.Delete(UpdateStateStore.DefaultPath);
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task A_batch_with_its_window_asks_github_once_and_still_ends_with_its_own_result()
    {
        using var github = new TcpListener(IPAddress.Loopback, 0);
        github.Start();
        var port = ((IPEndPoint)github.LocalEndpoint).Port;
        var request = AnswerOnceAsync(github, $"{{\"tag_name\":\"v{ReleaseVersion.Current}\"}}");
        var file = _dir.File("ảnh.txt");

        using var flevert = Start(file, $"http://127.0.0.1:{port}/latest");
        var exited = flevert.WaitForExitAsync();

        var asked = await request.WaitAsync(TimeSpan.FromSeconds(30));
        await exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.StartsWith("GET /latest ", asked, StringComparison.Ordinal);
        Assert.Contains($"User-Agent: Flevert/{ReleaseVersion.Current}", asked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie:", asked, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, flevert.ExitCode);
        Assert.True(File.Exists(_dir.Combine("ảnh (copy).txt")));
        Assert.NotNull(new UpdateStateStore(UpdateStateStore.DefaultPath).Load().LastCheck);
    }

    private Process Start(string file, string github)
    {
        var start = new ProcessStartInfo(ProcessBatchIntegrationTests.FlevertExe) { UseShellExecute = false, WorkingDirectory = _dir.Path };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("dev.copy");
        start.ArgumentList.Add(file);
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        start.Environment["FLEVERT_UPDATE_URL"] = github;
        start.Environment.Remove("FLEVERT_HEADLESS");
        return Process.Start(start)!;
    }

    /// <summary>A minimal HTTP/1.1 server: reads one request's head, answers with JSON, returns what it read.</summary>
    private static async Task<string> AnswerOnceAsync(TcpListener listener, string json)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var head = new StringBuilder();
        var buffer = new byte[4096];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var body = Encoding.UTF8.GetBytes(json);
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        await stream.WriteAsync(body);
        return head.ToString();
    }
}
