// ApiClient.cs
//
// Thin HTTP wrapper around the portal's "latest observation per game"
// endpoint. Returns per-game snapshots including player count and the
// raw room_nicknames string from the monitor's room properties.

using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MartianGamesAlerts;

internal sealed record GameSnapshot(
    DateTime ObservedAt,   // UTC
    int      PlayerCount,
    string   RoomNicknames);

internal sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ApiClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MartianGamesAlerts/1.0");
    }

    public async Task<Dictionary<string, GameSnapshot>> GetLatestPerGameAsync(CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/observations/latest-per-game";
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

        var records = JsonSerializer.Deserialize<List<LatestRecord>>(json, JsonOpts)
                      ?? new List<LatestRecord>();

        var result = new Dictionary<string, GameSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            if (string.IsNullOrEmpty(r.Game) || string.IsNullOrEmpty(r.ObservedAt)) continue;
            if (!TryParseServerDate(r.ObservedAt, out var dt)) continue;

            result[r.Game] = new GameSnapshot(
                ObservedAt:    dt,
                PlayerCount:   r.PlayerCount,
                RoomNicknames: r.RoomNicknames ?? "");
        }
        return result;
    }

    private static readonly string[] DateFormats = {
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.fffZ",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss",
    };

    private static bool TryParseServerDate(string s, out DateTime utc)
    {
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
            return true;

        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    public void Dispose() => _http.Dispose();

    private sealed class LatestRecord
    {
        [JsonPropertyName("game")]           public string  Game          { get; set; } = "";
        [JsonPropertyName("observed_at")]    public string  ObservedAt    { get; set; } = "";
        [JsonPropertyName("player_count")]   public int     PlayerCount   { get; set; }
        [JsonPropertyName("room_nicknames")] public string? RoomNicknames { get; set; }
    }
}
