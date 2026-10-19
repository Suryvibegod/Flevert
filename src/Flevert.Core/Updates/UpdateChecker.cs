using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Flevert.Core.Updates;

/// <summary>
/// Asks GitHub, at most once a day, whether a newer release than this build is published. Never downloads or runs
/// anything: the caller only offers to open <see cref="DownloadPage"/>. docs/specs/core.md "Báo bản mới", ADR-0006.
/// </summary>
public sealed class UpdateChecker(
    HttpMessageHandler handler, UpdateStateStore store, TimeProvider clock, ReleaseVersion current, Uri endpoint, TimeSpan? timeout = null)
{
    private const int MaxAnswerBytes = 1024 * 1024;
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    /// <summary>Short enough that the answer arrives while the batch's notification is still showing (BatchUi).</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Ignores drafts and pre-releases, so a release withdrawn by marking it pre-release is not offered.</summary>
    public static Uri LatestReleaseApi { get; } = new("https://api.github.com/repos/Suryvibegod/Flevert/releases/latest");

    /// <summary>The only page Flevert ever opens for an update; never a URL taken from GitHub's answer.</summary>
    public static Uri DownloadPage { get; } = new("https://github.com/Suryvibegod/Flevert/releases/latest");

    /// <returns>The newer version to offer, or null: none, turned off, asked within the last day, skipped, or any failure.</returns>
    public async Task<ReleaseVersion?> CheckAsync(CancellationToken cancellationToken)
    {
        var state = store.Load();
        var now = clock.GetUtcNow();
        if (!state.Enabled || (state.LastCheck is { } last && last <= now && now - last < Interval))
            return null;
        // Recorded before asking, so a failing or slow network is not asked again until tomorrow either.
        TrySave(state with { LastCheck = now });

        var latest = await LatestAsync(cancellationToken).ConfigureAwait(false);
        return latest is { } version && version > current && version.ToString() != state.Skipped ? version : null;
    }

    /// <summary>Remembers "Skip this version"; a state file that cannot be read is left alone (it counts as turned off).</summary>
    public void Skip(ReleaseVersion version)
    {
        var state = store.Load();
        if (!ReferenceEquals(state, UpdateState.Unreadable))
            TrySave(state with { Skipped = version.ToString() });
    }

    /// <summary>For update-check on|off: throws if the setting cannot be saved, so the command can say so.</summary>
    public static void SetEnabled(UpdateStateStore store, bool enabled)
    {
        var state = store.Load();
        // Setup chose its default from this file; if it could not read it either, "on" might undo the user's "off".
        if (ReferenceEquals(state, UpdateState.Unreadable))
            throw new IOException("The update-check setting cannot be read right now; it was left unchanged.");
        store.Save(state with { Enabled = enabled });
    }

    /// <summary>
    /// FLEVERT_UPDATE_URL, for tests that run a fake GitHub: plain http to a loopback IP address only, so that no name
    /// lookup can send it elsewhere.
    /// </summary>
    public static bool TryTestEndpoint(string? url, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
            || uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
            return false;
        endpoint = uri;
        return true;
    }

    private async Task<ReleaseVersion?> LatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = _timeout, MaxResponseContentBufferSize = MaxAnswerBytes };
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Flevert", current.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            // ResponseContentRead buffers the body within MaxResponseContentBufferSize and the timeout, or throws.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
                && ReleaseVersion.TryParseTag(tag.GetString(), out var version)
                ? version
                : null;
        }
#pragma warning disable CA1031 // Any failure (network, proxy, TLS, a malformed answer) just means nothing to offer today.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private void TrySave(UpdateState state)
    {
        try
        {
            store.Save(state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
