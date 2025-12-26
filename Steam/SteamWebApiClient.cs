using System.Net.Http.Json;

namespace BanCheckPlugin.Steam;

public sealed class SteamWebApiClient
{
    private readonly HttpClient _http;

    public SteamWebApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<SteamPlayerSummary?> GetPlayerSummaryAsync(string apiKey, string steamId64, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId64))
            return null;

        // ISteamUser/GetPlayerSummaries/v2
        var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(apiKey)}&steamids={Uri.EscapeDataString(steamId64)}";

        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var payload = await resp.Content.ReadFromJsonAsync<PlayerSummariesResponse>(cancellationToken: ct).ConfigureAwait(false);
        return payload?.Response?.Players?.FirstOrDefault();
    }
}
