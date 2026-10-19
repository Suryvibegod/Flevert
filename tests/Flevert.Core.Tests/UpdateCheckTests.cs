using System.Net;
using System.Text;
using Flevert.Core.Updates;

namespace Flevert.Core.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("v1.10.3", 1, 10, 3)]
    [InlineData("v12345.0.99999", 12345, 0, 99999)]
    public void A_release_tag_names_its_version(string tag, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParseTag(tag, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.2.0")]
    [InlineData("v0.2")]
    [InlineData("v0.2.0-rc1")]
    [InlineData("v0.2.0.1")]
    [InlineData(" v0.2.0")]
    [InlineData("v0.2.0\n")]
    [InlineData("V0.2.0")]
    [InlineData("v123456.0.0")] // more than five digits
    [InlineData("v٠.٢.٠")] // Arabic-Indic digits: .NET's \d would accept them
    [InlineData("v0.２.0")] // full-width digit
    public void Anything_but_vMAJOR_MINOR_PATCH_in_ASCII_digits_is_not_a_release(string? tag)
    {
        Assert.False(ReleaseVersion.TryParseTag(tag, out _));
    }

    [Fact]
    public void Versions_compare_by_number_not_by_text()
    {
        Assert.True(new ReleaseVersion(0, 10, 0) > new ReleaseVersion(0, 9, 9));
        Assert.True(new ReleaseVersion(1, 0, 0) > new ReleaseVersion(0, 99, 99));
        Assert.True(new ReleaseVersion(0, 2, 1) > new ReleaseVersion(0, 2, 0));
        Assert.False(new ReleaseVersion(0, 2, 0) > new ReleaseVersion(0, 2, 0));
        Assert.Equal("0.2.0", new ReleaseVersion(0, 2, 0).ToString());
    }

    [Fact]
    public void The_running_version_comes_from_the_build()
    {
        var expected = typeof(ReleaseVersion).Assembly.GetName().Version!;
        Assert.Equal(new ReleaseVersion(expected.Major, expected.Minor, expected.Build), ReleaseVersion.Current);
    }
}

public sealed class UpdateStateStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_missing_file_means_enabled_and_never_checked()
    {
        var state = new UpdateStateStore(_dir.Combine(@"update\state.json")).Load();

        Assert.Equal(new UpdateState(Enabled: true, LastCheck: null, Skipped: null), state);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"enabled\": \"yes\"}")]
    [InlineData("{\"lastCheck\": \"tomorrow\"}")]
    public void A_damaged_file_means_the_defaults(string content)
    {
        var path = _dir.File("state.json", content);

        Assert.Equal(new UpdateState(true, null, null), new UpdateStateStore(path).Load());
    }

    [Fact]
    public void What_is_saved_is_loaded_back_and_no_temporary_file_is_left()
    {
        var path = _dir.Combine(@"update\state.json");
        var store = new UpdateStateStore(path);
        var saved = new UpdateState(false, new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero), "0.2.0");

        store.Save(saved);
        store.Save(saved with { Skipped = "0.3.0" });

        Assert.Equal(saved with { Skipped = "0.3.0" }, store.Load());
        Assert.Equal(["state.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }
}

public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly ReleaseVersion Running = new(0, 1, 0);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly FakeGitHub _github = new();
    private readonly FixedClock _clock = new(Now);
    private readonly UpdateStateStore _store;

    public UpdateCheckerTests() => _store = new UpdateStateStore(_dir.Combine(@"update\state.json"));

    public void Dispose()
    {
        _github.Dispose();
        _dir.Dispose();
    }

    private UpdateChecker Checker() => new(_github, _store, _clock, Running, UpdateChecker.LatestReleaseApi);

    [Fact]
    public async Task A_newer_release_is_offered_and_the_request_says_only_who_is_asking()
    {
        _github.Tag = "v0.2.0";

        Assert.Equal(new ReleaseVersion(0, 2, 0), await Checker().CheckAsync(CancellationToken.None));

        var request = Assert.Single(_github.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("https://api.github.com/repos/Suryvibegod/Flevert/releases/latest"), request.RequestUri);
        Assert.Equal("Flevert/0.1.0", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("v0.0.9")]
    [InlineData("v0.2.0-rc1")]
    [InlineData("v٠.٢.٠")]
    public async Task The_same_an_older_or_a_malformed_release_is_not_offered(string tag)
    {
        _github.Tag = tag;

        Assert.Null(await Checker().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task It_asks_at_most_once_a_day_even_when_the_request_failed()
    {
        _github.Status = HttpStatusCode.InternalServerError;
        await Checker().CheckAsync(CancellationToken.None);
        _github.Status = HttpStatusCode.OK;
        _github.Tag = "v0.2.0";

        _clock.Now = Now.AddHours(23);
        Assert.Null(await Checker().CheckAsync(CancellationToken.None));
        Assert.Single(_github.Requests);

        _clock.Now = Now.AddHours(24);
        Assert.Equal(new ReleaseVersion(0, 2, 0), await Checker().CheckAsync(CancellationToken.None));
        Assert.Equal(2, _github.Requests.Count);
    }

    [Fact]
    public async Task A_last_check_in_the_future_does_not_block_checking()
    {
        _store.Save(new UpdateState(true, Now.AddDays(30), null));
        _github.Tag = "v0.2.0";

        Assert.Equal(new ReleaseVersion(0, 2, 0), await Checker().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Turned_off_means_no_request_at_all()
    {
        var checker = Checker();
        checker.SetEnabled(false);
        _github.Tag = "v0.2.0";

        Assert.Null(await checker.CheckAsync(CancellationToken.None));
        Assert.Empty(_github.Requests);

        checker.SetEnabled(true);
        Assert.Equal(new ReleaseVersion(0, 2, 0), await checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_skipped_version_is_not_offered_again_but_a_later_one_is()
    {
        _github.Tag = "v0.2.0";
        var checker = Checker();
        checker.Skip(new ReleaseVersion(0, 2, 0));

        Assert.Null(await checker.CheckAsync(CancellationToken.None));

        _clock.Now = Now.AddDays(1);
        _github.Tag = "v0.2.1";
        Assert.Equal(new ReleaseVersion(0, 2, 1), await checker.CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")] // no release published yet
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{\"name\":\"v0.2.0\"}")]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":2}")]
    [InlineData(HttpStatusCode.OK, "[{\"tag_name\":\"v0.2.0\"}]")]
    public async Task An_unusable_answer_offers_nothing(HttpStatusCode status, string body)
    {
        _github.Status = status;
        _github.Body = body;

        Assert.Null(await Checker().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_answer_over_one_megabyte_is_not_read()
    {
        _github.Body = "{\"tag_name\":\"v0.2.0\",\"body\":\"" + new string('x', 1024 * 1024) + "\"}";

        Assert.Null(await Checker().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task No_network_offers_nothing()
    {
        _github.Throw = new HttpRequestException("no route to host");

        Assert.Null(await Checker().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_silent_server_is_given_up_on_after_the_timeout()
    {
        _github.Delay = TimeSpan.FromSeconds(30);
        var checker = new UpdateChecker(_github, _store, _clock, Running, UpdateChecker.LatestReleaseApi, TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(await checker.CheckAsync(CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_state_file_that_cannot_be_written_still_lets_the_check_run()
    {
        // A folder where the state file should be: saving fails, checking must not.
        Directory.CreateDirectory(_dir.Combine(@"update\state.json"));
        _github.Tag = "v0.2.0";

        Assert.Equal(new ReleaseVersion(0, 2, 0), await Checker().CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/latest", true)]
    [InlineData("http://[::1]:5000/latest", true)]
    [InlineData("http://localhost:5000/latest", false)] // a name could resolve elsewhere
    [InlineData("http://example.com/latest", false)]
    [InlineData("https://evil.example/latest", false)]
    [InlineData("file:///C:/x.json", false)]
    [InlineData("not a url", false)]
    public void A_test_endpoint_is_accepted_only_on_the_loopback_address(string url, bool accepted)
    {
        Assert.Equal(accepted, UpdateChecker.TryTestEndpoint(url, out _));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string? Tag { get; set; }

        public string? Body { get; set; }

        public TimeSpan Delay { get; set; }

        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Throw is not null)
                throw Throw;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
            var body = Body ?? $"{{\"tag_name\":\"{Tag}\",\"draft\":false,\"prerelease\":false}}";
            return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
