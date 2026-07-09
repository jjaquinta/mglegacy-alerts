// ManifestClient.cs
//
// Fetches and parses /pc/manifest.json — the shell-script-generated
// listing of downloadable products (games, alerter, launcher) plus a
// hand-edited news feed.
//
// No auth: the manifest is public. Just a GET + JSON parse.

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MartianGamesAlerts;

internal sealed record Product(
    string   Url,
    string   Sha256,
    long     SizeBytes,
    DateTime UpdatedAt);

// A single event on the news feed. Kept named "NewsItem" for continuity;
// semantically these are events now, not general news. PostedAt is the
// event's START time in UTC (format: "yyyy-MM-dd HH:mm:ss"); Game is a
// ManifestId identifying which product the event is for.
internal sealed record NewsItem(
    string  PostedAt,
    string  Title,
    string  Body,
    string? Game);

internal sealed record Manifest(
    IReadOnlyDictionary<string, Product> Products,
    IReadOnlyList<NewsItem>              News);

internal sealed class ManifestClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _manifestUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ManifestClient(string manifestUrl)
    {
        _manifestUrl = manifestUrl ?? throw new ArgumentNullException(nameof(manifestUrl));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MartianGamesAlerts/1.0");
    }

    public async Task<Manifest?> FetchAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(_manifestUrl, ct).ConfigureAwait(false);
        var raw = JsonSerializer.Deserialize<RawManifest>(json, JsonOpts);
        if (raw == null) return null;

        var products = new Dictionary<string, Product>(
            raw.Products?.Count ?? 0, StringComparer.OrdinalIgnoreCase);
        if (raw.Products != null)
        {
            foreach (var (id, p) in raw.Products)
            {
                if (p == null || string.IsNullOrEmpty(p.Url)) continue;
                products[id] = new Product(
                    Url:       p.Url,
                    Sha256:    p.Sha256 ?? "",
                    SizeBytes: p.SizeBytes,
                    UpdatedAt: p.UpdatedAt);
            }
        }

        var news = new List<NewsItem>(raw.News?.Count ?? 0);
        if (raw.News != null)
        {
            foreach (var n in raw.News)
            {
                if (n == null || string.IsNullOrEmpty(n.Title)) continue;
                news.Add(new NewsItem(
                    PostedAt: n.PostedAt ?? "",
                    Title:    n.Title,
                    Body:     n.Body ?? "",
                    Game:     n.Game));
            }
        }

        return new Manifest(products, news);
    }

    public void Dispose() => _http.Dispose();

    // ---- JSON wire types (nullable-tolerant) ----

    private sealed class RawManifest
    {
        [JsonPropertyName("products")] public Dictionary<string, RawProduct>? Products { get; set; }
        [JsonPropertyName("news")]     public List<RawNews>?                  News     { get; set; }
    }

    private sealed class RawProduct
    {
        [JsonPropertyName("url")]        public string?  Url       { get; set; }
        [JsonPropertyName("sha256")]     public string?  Sha256    { get; set; }
        [JsonPropertyName("size_bytes")] public long     SizeBytes { get; set; }
        [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
    }

    private sealed class RawNews
    {
        [JsonPropertyName("posted_at")] public string? PostedAt { get; set; }
        [JsonPropertyName("title")]     public string? Title    { get; set; }
        [JsonPropertyName("body")]      public string? Body     { get; set; }
        [JsonPropertyName("game")]      public string? Game     { get; set; }
    }
}