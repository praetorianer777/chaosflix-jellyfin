using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Api;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Chaosflix.Tests.Contract;

/// <summary>
/// Fetches everything the contract tests look at, once, and hands the responses to the test class.
///
/// media.ccc.de is somebody else's free public service, so the whole suite is a fixed, small number
/// of requests (see <see cref="RequestCount"/>) issued sequentially with a pause between them, and
/// it never downloads a whole recording — only the first kilobyte, to prove range requests work.
/// </summary>
public sealed class CccApiLiveFixture : IAsyncLifetime
{
    /// <summary>Upper bound on the requests one full contract run sends to media.ccc.de.</summary>
    public const int RequestCount = 8;

    /// <summary>Search term used to exercise /public/events/search; broad enough to always hit.</summary>
    public const string SearchQuery = "chaos";

    /// <summary>A conference is only used as the sample if it has at least this many events.</summary>
    private const int MinimumConferenceSize = 10;

    private const int MaxConferenceAttempts = 3;

    /// <summary>Deliberate pause between requests so a run never looks like a burst.</summary>
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ContractHttpClientFactory _factory = new();
    private CccApiClient? _client;
    private bool _first = true;

    /// <summary>Gets the full /public/conferences response.</summary>
    public IReadOnlyList<CccConference> Conferences { get; private set; } = Array.Empty<CccConference>();

    /// <summary>Gets the conference whose detail response was sampled.</summary>
    public CccConference? ConferenceDetail { get; private set; }

    /// <summary>Gets the acronym that <see cref="ConferenceDetail"/> was requested with.</summary>
    public string ConferenceAcronym { get; private set; } = string.Empty;

    /// <summary>Gets the event detail response that was sampled.</summary>
    public CccEvent? EventDetail { get; private set; }

    /// <summary>Gets the /public/events/search response.</summary>
    public IReadOnlyList<CccEvent> SearchResults { get; private set; } = Array.Empty<CccEvent>();

    /// <summary>Gets the cdn.media.ccc.de response to a HEAD on a recording_url.</summary>
    public CdnProbe? Cdn { get; private set; }

    /// <summary>Gets the error that aborted the fetch, if any.</summary>
    public Exception? SetupFailure { get; private set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // The fixture is constructed even when every test in the class is skipped, so the opt-in
        // has to be re-checked here or an unset CCC_CONTRACT would still hit the network.
        if (!ContractFactAttribute.IsEnabled)
        {
            return;
        }

        try
        {
            await FetchAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetupFailure = ex;
        }
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Throws the setup failure, if the fetch could not complete. Every test calls this first so a
    /// media.ccc.de outage fails with "could not reach it" rather than "field x is missing".
    /// </summary>
    public void EnsureFetched()
    {
        if (SetupFailure != null)
        {
            throw new InvalidOperationException(
                "Could not fetch the media.ccc.de responses the contract tests inspect. "
                + "This is a reachability problem, not necessarily a contract break.",
                SetupFailure);
        }
    }

    private async Task FetchAsync()
    {
        using var cts = new CancellationTokenSource(Timeout * RequestCount);
        var ct = cts.Token;

        _client = new CccApiClient(_factory, NullLogger<CccApiClient>.Instance);

        await SpaceAsync(ct).ConfigureAwait(false);
        Conferences = await _client.GetConferencesAsync(ct).ConfigureAwait(false);

        var candidates = Conferences
            .Where(c => c.EventLastReleasedAt != null && !string.IsNullOrWhiteSpace(c.Acronym))
            .OrderByDescending(c => c.EventLastReleasedAt)
            .Take(MaxConferenceAttempts)
            .ToList();

        foreach (var candidate in candidates)
        {
            await SpaceAsync(ct).ConfigureAwait(false);
            var detail = await _client.GetConferenceAsync(candidate.Acronym, ct).ConfigureAwait(false);
            ConferenceDetail = detail;
            ConferenceAcronym = candidate.Acronym;

            if (detail?.Events is { Count: >= MinimumConferenceSize })
            {
                break;
            }
        }

        // The most-watched talk of a conference is the one that reliably carries the full set of
        // encodings, which is what the recording assertions need to be meaningful.
        var sample = ConferenceDetail?.Events?
            .Where(e => !string.IsNullOrWhiteSpace(e.Guid))
            .OrderByDescending(e => e.ViewCount)
            .FirstOrDefault();

        if (sample != null)
        {
            await SpaceAsync(ct).ConfigureAwait(false);
            EventDetail = await _client.GetEventAsync(sample.Guid, ct).ConfigureAwait(false);
        }

        await SpaceAsync(ct).ConfigureAwait(false);
        SearchResults = await _client.SearchEventsAsync(SearchQuery, ct).ConfigureAwait(false);

        var recording = EventDetail?.Recordings?
            .FirstOrDefault(r => r.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase)
                && r.RecordingUrl.Contains("cdn.media.ccc.de", StringComparison.OrdinalIgnoreCase));

        if (recording != null)
        {
            Cdn = await ProbeCdnAsync(recording.RecordingUrl, ct).ConfigureAwait(false);
        }
    }

    private async Task<CdnProbe> ProbeCdnAsync(string recordingUrl, CancellationToken ct)
    {
        var redirectClient = _factory.CreateClient(CccApiClient.RedirectClientName);

        await SpaceAsync(ct).ConfigureAwait(false);
        using var head = new HttpRequestMessage(HttpMethod.Head, recordingUrl);
        using var headResponse = await redirectClient
            .SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var location = headResponse.Headers.Location;
        var mirrorUrl = location == null
            ? recordingUrl
            : (location.IsAbsoluteUri ? location : new Uri(new Uri(recordingUrl), location)).ToString();

        await SpaceAsync(ct).ConfigureAwait(false);
        using var range = new HttpRequestMessage(HttpMethod.Get, mirrorUrl);
        range.Headers.Range = new RangeHeaderValue(0, 1023);
        using var rangeResponse = await _factory.CreateClient(nameof(CccApiClient))
            .SendAsync(range, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        var body = await rangeResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        return new CdnProbe(
            recordingUrl,
            (int)headResponse.StatusCode,
            mirrorUrl,
            location != null,
            (int)rangeResponse.StatusCode,
            rangeResponse.Content.Headers.ContentRange?.ToString(),
            body.Length);
    }

    private async Task SpaceAsync(CancellationToken ct)
    {
        if (_first)
        {
            _first = false;
            return;
        }

        await Task.Delay(RequestSpacing, ct).ConfigureAwait(false);
    }

    /// <summary>What cdn.media.ccc.de answered for one recording.</summary>
    /// <param name="RecordingUrl">The cdn.media.ccc.de URL that was probed.</param>
    /// <param name="HeadStatus">Status of the HEAD request (expected: a 3xx).</param>
    /// <param name="MirrorUrl">The URL the CDN redirected to, or the original if it did not.</param>
    /// <param name="Redirected">Whether the CDN sent a Location header.</param>
    /// <param name="RangeStatus">Status of the ranged GET against the mirror (expected: 206).</param>
    /// <param name="ContentRange">The mirror's Content-Range header, if any.</param>
    /// <param name="BytesReturned">How many bytes the ranged GET actually returned.</param>
    public sealed record CdnProbe(
        string RecordingUrl,
        int HeadStatus,
        string MirrorUrl,
        bool Redirected,
        int RangeStatus,
        string? ContentRange,
        int BytesReturned);

    private sealed class ContractHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _default = new(new HttpClientHandler { AllowAutoRedirect = true });
        private readonly HttpClient _noRedirect = new(new HttpClientHandler { AllowAutoRedirect = false });

        public ContractHttpClientFactory()
        {
            foreach (var client in new[] { _default, _noRedirect })
            {
                client.Timeout = Timeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            }
        }

        /// <summary>
        /// Identifies the suite to media.ccc.de operators, with somewhere to complain to.
        /// </summary>
        private static string UserAgent =>
            "chaosflix-jellyfin-contract-tests/1.0 "
            + "(+https://github.com/praetorianer777/chaosflix-jellyfin)";

        public HttpClient CreateClient(string name) =>
            name == CccApiClient.RedirectClientName ? _noRedirect : _default;

        public void Dispose()
        {
            _default.Dispose();
            _noRedirect.Dispose();
        }
    }
}
