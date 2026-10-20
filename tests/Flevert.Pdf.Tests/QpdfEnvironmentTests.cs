using System.IO.Pipes;
using System.Text;
using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary>Runs alone: the test changes the test process's own environment, which every qpdf it starts inherits.</summary>
[CollectionDefinition(nameof(QpdfEnvironmentTests), DisableParallelization = true)]
public sealed class QpdfEnvironmentRunsAlone;

[Collection(nameof(QpdfEnvironmentTests))]
public sealed class QpdfEnvironmentTests : IDisposable
{
    private const string OpenSslConf = "OPENSSL_CONF";
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Qpdf_does_not_read_the_openssl_configuration_the_users_environment_names()
    {
        // OpenSSL, linked into qpdf, loads the file OPENSSL_CONF names, and that file can load further code. A named pipe
        // stands in for it, so the test sees whether qpdf opens it (qpdf 12.4.2 does even for a rotation).
        var input = _dir.Combine("in.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);
        var pipe = "flevert-test-" + Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable(OpenSslConf);
        Environment.SetEnvironmentVariable(OpenSslConf, @"\\.\pipe\" + pipe);
        try
        {
            var lines = Encoding.UTF8.GetBytes($"{input}\n{_dir.Combine("direct.pdf")}\n");
            var openedByQpdfItself = await OpensPipe(pipe, () => ExternalTool.RunAsync(
                Qpdf.NextToApp.ExePath, ["--rotate=+90:1-z", "@-"], TimeSpan.FromSeconds(30), lines));
            var openedThroughWrapper = await OpensPipe(pipe, () => Qpdf.NextToApp.TransformAsync(
                input, _dir.Combine("wrapped.pdf"), ["--rotate=+90:1-z"], CancellationToken.None));

            // Control: qpdf started with the inherited environment opens the file, so the pipe would see it.
            Assert.True(openedByQpdfItself, "qpdf run directly did not open OPENSSL_CONF: the test proves nothing");
            Assert.False(openedThroughWrapper, "qpdf opened the file OPENSSL_CONF names");
            Assert.True(File.Exists(_dir.Combine("wrapped.pdf")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenSslConf, previous);
        }
    }

    [Fact]
    public void Every_openssl_setting_that_can_load_code_is_removed()
    {
        // The modules and engines folders only matter through a configuration, which the test above covers end to end.
        Assert.Equal(["OPENSSL_CONF", "OPENSSL_ENGINES", "OPENSSL_MODULES"], Qpdf.ToolEnvironment.Keys.Order(StringComparer.Ordinal));
        Assert.All(Qpdf.ToolEnvironment.Values, Assert.Null);
    }

    // The server closes as soon as a client connects, so OpenSSL reads an empty file instead of waiting on the pipe.
    private static async Task<bool> OpensPipe(string name, Func<Task> run)
    {
        var server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connection = server.WaitForConnectionAsync();
        _ = connection.ContinueWith(t =>
        {
            _ = t.Exception; // a wait ended by closing the server
            server.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await run();
            return connection.IsCompletedSuccessfully;
        }
        finally
        {
            server.Dispose();
        }
    }
}
