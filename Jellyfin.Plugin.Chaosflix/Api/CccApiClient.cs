using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Api;

/// <summary>
/// HTTP client for the media.ccc.de public API.
/// </summary>
public class CccApiClient : IDisposable
{
    private const string BaseUrl = "https://api.media.ccc.de/public";
    private readonly HttpClient _httpClient;
    private readonly ILogger<CccApiClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CccApiClient"/> class.
    /// </summary>
    public CccApiClient(IHttpClientFactory httpClientFactory, ILogger<CccApiClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(CccApiClient));
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        _logger = logger;
    }

    /// <summary>
    /// Gets all conferences.
    /// </summary>
    public async Task<List<CccConference>> GetConferencesAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching conferences from CCC API");
        var response = await _httpClient
            .GetFromJsonAsync<CccConferencesResponse>("/public/conferences", cancellationToken)
            .ConfigureAwait(false);
        return response?.Conferences ?? new List<CccConference>();
    }

    /// <summary>
    /// Gets a single conference with its events.
    /// </summary>
    public async Task<CccConference?> GetConferenceAsync(string acronym, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching conference {Acronym} from CCC API", acronym);
        return await _httpClient
            .GetFromJsonAsync<CccConference>($"/public/conferences/{acronym}", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a single event with its recordings.
    /// </summary>
    public async Task<CccEvent?> GetEventAsync(string guid, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching event {Guid} from CCC API", guid);
        return await _httpClient
            .GetFromJsonAsync<CccEvent>($"/public/events/{guid}", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Searches for events.
    /// </summary>
    public async Task<List<CccEvent>> SearchEventsAsync(string query, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Searching CCC API for {Query}", query);
        var encoded = Uri.EscapeDataString(query);
        var response = await _httpClient
            .GetFromJsonAsync<CccEventsResponse>($"/public/events/search?q={encoded}", cancellationToken)
            .ConfigureAwait(false);
        return response?.Events ?? new List<CccEvent>();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
