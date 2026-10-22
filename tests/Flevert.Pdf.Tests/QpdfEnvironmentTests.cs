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

    [WindowsFact]
    [SupportedOSPlatform("windows")]
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
            var openedByQpdfItself = await OpensPipe(pipe, () => RunDirectly(input, _dir.Combine("direct.pdf")));
            var openedThroughWrapper = await OpensPipe(pipe, () => Qpdf.NextToApp.TransformAsync(
                input, _dir.Combine("wrapped.pdf"), ["--rotate=+90:1-z"], CancellationToken.None));

            // Control: qpdf started with the inherited environment, under the same limits, opens the file, so the pipe
            // would see it.
            Assert.True(openedByQpdfItself, "qpdf run directly did not open OPENSSL_CONF: the test proves nothing");
            Assert.False(openedThroughWrapper, "qpdf opened the file OPENSSL_CONF names");
            Assert.True(File.Exists(_dir.Combine("wrapped.pdf")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenSslConf, previous);
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Openssl_reads_only_the_empty_configuration_beside_qpdf_and_loads_code_only_from_its_folder()
    {
        // Review SA-L1: without OPENSSL_CONF, OpenSSL reads C:\Program Files\Common Files\SSL\openssl.cnf, and C: need
        // not be the system drive. The modules and engines folders matter only through a configuration.
        var folder = Path.GetDirectoryName(Qpdf.NextToApp.ExePath)!;
        var environment = Qpdf.NextToApp.ToolEnvironment();

        Assert.Equal(Path.Combine(folder, "openssl.cnf"), environment["OPENSSL_CONF"]);
        Assert.Equal(folder, environment["OPENSSL_MODULES"]);
        Assert.Equal(folder, environment["OPENSSL_ENGINES"]);
        // Review DD-5: paths a configuration's .include would read relative to.
        Assert.Null(environment["OPENSSL_CONF_INCLUDE"]);
        var settings = File.ReadAllLines(Path.Combine(folder, "openssl.cnf")).Where(line => line.Trim() is { Length: > 0 } text && text[0] != '#');
        Assert.Empty(settings);
    }

    [Fact]
    public async Task Qpdf_ignores_the_qpdf_settings_of_the_users_environment()
    {
        // Review SA-I1: QPDF_CRYPTO_PROVIDER picks qpdf's crypto, QPDF_ZOPFLI its compressor. An unknown provider makes
        // qpdf fail as soon as it hashes, which a rotation does.
        var input = _dir.Combine("in.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);
        var name = "QPDF_CRYPTO_PROVIDER";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "flevert-test-no-such-provider");
        try
        {
            var direct = await RunDirectly(input, _dir.Combine("direct.pdf"));
            // Control: qpdf started with the inherited environment, under the same limits, fails on that variable.
            Assert.Equal(2, direct.ExitCode);
            Assert.Contains("flevert-test-no-such-provider", direct.StandardError, StringComparison.Ordinal);

            await Qpdf.NextToApp.TransformAsync(input, _dir.Combine("wrapped.pdf"), ["--rotate=+90:1-z"], CancellationToken.None);

            Assert.True(File.Exists(_dir.Combine("wrapped.pdf")));
            Assert.True(Qpdf.NextToApp.ToolEnvironment().TryGetValue(name.ToLowerInvariant(), out var value) && value is null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public async Task Qpdf_still_encrypts_with_aes_256_under_that_configuration()
    {
        // T14 needs AES-256 from the default crypto provider (OpenSSL) with the empty configuration.
        var input = _dir.Combine("in.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);
        var output = _dir.Combine("locked.pdf");

        await Qpdf.NextToApp.TransformAsync(input, output, ["--encrypt", "user", "owner", "256", "--"], CancellationToken.None);

        var show = await ExternalTool.RunAsync(Qpdf.NextToApp.ExePath, ["--password=user", "--show-encryption", output], TimeSpan.FromSeconds(30));
        Assert.Contains("stream encryption method: AESv3", show.StandardOutput, StringComparison.Ordinal);
    }

    // qpdf as the wrapper runs it (same paths, limits and timeout) but with the test process's environment as it is, so
    // a control differs from the wrapped run in the environment alone.
    private static Task<ExternalToolResult> RunDirectly(string input, string output)
    {
        var lines = Encoding.UTF8.GetBytes($"{Qpdf.ToExtendedPath(input)}\n{Qpdf.ToExtendedPath(output)}\n");
        var limits = new ExternalToolLimits { MaxMemoryBytes = Qpdf.MaxMemoryFor(new FileInfo(input).Length), SingleProcess = true };
        return ExternalTool.RunAsync(Qpdf.NextToApp.ExePath, ["--rotate=+90:1-z", "@-"], Qpdf.TimeoutFor(new FileInfo(input).Length), lines, limits: limits);
    }

    // The server closes as soon as a client connects, so OpenSSL reads an empty file instead of waiting on the pipe. qpdf
    // opens the file before it exits, but the connection is awaited briefly in case its completion is still on its way.
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
            await Task.WhenAny(connection, Task.Delay(TimeSpan.FromMilliseconds(500)));
            return connection.IsCompletedSuccessfully;
        }
        finally
        {
            server.Dispose();
        }
    }
}
