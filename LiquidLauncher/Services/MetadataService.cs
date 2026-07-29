using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiquidLauncher.Services;

/// <summary>One search hit the user can pick from, before we've downloaded anything.</summary>
public class GameMetadataCandidate
{
    public required string Source { get; init; }       // "Steam" or "PSN"
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ThumbnailUrl { get; init; }
}

/// <summary>Result of a full metadata lookup, ready to drop into a GameEntry.</summary>
public class GameMetadata
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Genre { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Platform { get; set; }
    public string? CoverImagePath { get; set; }
    public string? BackgroundImagePath { get; set; }
}

/// <summary>
/// Looks up game info across multiple keyless public APIs (Steam store, PSN)
/// so the user gets several candidates to choose from instead of a single guess.
/// Cover/background art is downloaded once into %AppData%\LiquidLauncher\Metadata
/// so the library still works offline after the initial fetch.
/// </summary>
public static class MetadataService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LiquidLauncher", "Metadata");

    static MetadataService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("LiquidLauncher/1.0");
    }

    /// <summary>
    /// Searches every source in parallel and returns up to <paramref name="maxPerSource"/>
    /// candidates from each, so the caller can present a picker instead of guessing.
    /// </summary>
    public static async Task<List<GameMetadataCandidate>> SearchAsync(string query, int maxPerSource = 5)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<GameMetadataCandidate>();

        var steamTask = SafeSearchSteamAsync(query, maxPerSource);
        var psnTask = SafeSearchPsnAsync(query, maxPerSource);

        await Task.WhenAll(steamTask, psnTask);

        var results = new List<GameMetadataCandidate>();
        results.AddRange(steamTask.Result);
        results.AddRange(psnTask.Result);
        return results;
    }

    /// <summary>Fetches full details for a candidate the user picked from SearchAsync results.</summary>
    public static async Task<GameMetadata?> FetchDetailsAsync(GameMetadataCandidate candidate)
    {
        try
        {
            return candidate.Source switch
            {
                "Steam" => await FetchSteamDetailsAsync(int.Parse(candidate.Id)),
                "PSN" => await FetchPsnDetailsAsync(candidate.Id),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Convenience one-shot lookup for callers that just want the best guess
    /// (kept for backwards compatibility) — prefer SearchAsync + FetchDetailsAsync for a picker.</summary>
    public static async Task<GameMetadata?> FetchAsync(string query)
    {
        var candidates = await SearchAsync(query, maxPerSource: 1);
        var best = candidates.FirstOrDefault(c => string.Equals(c.Name, query, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault();
        return best is null ? null : await FetchDetailsAsync(best);
    }

    // ---------------- Steam ----------------

    private static async Task<List<GameMetadataCandidate>> SafeSearchSteamAsync(string query, int max)
    {
        try { return await SearchSteamAsync(query, max); }
        catch { return new List<GameMetadataCandidate>(); }
    }

    private static async Task<List<GameMetadataCandidate>> SearchSteamAsync(string query, int max)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&cc=us&l=en";
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return new List<GameMetadataCandidate>();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("items", out var items)) return new List<GameMetadataCandidate>();

        var results = new List<GameMetadataCandidate>();
        foreach (var item in items.EnumerateArray().Take(max))
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : (int?)null;
            var name = TryGetString(item, "name");
            if (id is null || name is null) continue;

            results.Add(new GameMetadataCandidate
            {
                Source = "Steam",
                Id = id.Value.ToString(),
                Name = name,
                ThumbnailUrl = TryGetString(item, "tiny_image")
            });
        }
        return results;
    }

    private static async Task<GameMetadata?> FetchSteamDetailsAsync(int appId)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=us&l=en";
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty(appId.ToString(), out var entry)) return null;
        if (!entry.TryGetProperty("success", out var successEl) || !successEl.GetBoolean()) return null;
        if (!entry.TryGetProperty("data", out var data)) return null;

        var result = new GameMetadata
        {
            Name = TryGetString(data, "name"),
            Description = TryGetString(data, "short_description"),
            Platform = "PC (Windows)"
        };

        if (data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
        {
            result.Genre = string.Join(", ", genres.EnumerateArray()
                .Select(g => TryGetString(g, "description"))
                .Where(g => !string.IsNullOrWhiteSpace(g)));
        }

        if (data.TryGetProperty("release_date", out var releaseDate))
            result.ReleaseDate = TryGetString(releaseDate, "date");

        // header_image is a wide banner -> background; capsule_image is a tall
        // poster-ish thumbnail -> cover. Both are downloaded and cached locally.
        var headerUrl = TryGetString(data, "header_image");
        var coverUrl = TryGetString(data, "capsule_image") ?? headerUrl;

        if (!string.IsNullOrWhiteSpace(headerUrl))
            result.BackgroundImagePath = await DownloadImageAsync(headerUrl!, "steam", appId.ToString(), "bg");

        if (!string.IsNullOrWhiteSpace(coverUrl))
            result.CoverImagePath = await DownloadImageAsync(coverUrl!, "steam", appId.ToString(), "cover");

        return result;
    }

    // ---------------- PSN (PlayStation Store) ----------------
    // store.playstation.com has no public API/key, so this scrapes the search and
    // product pages the same way XenorPLxx/playnite-metadata-psn-universal does:
    // https://github.com/XenorPLxx/playnite-metadata-psn-universal
    // The "Id" we carry around for a PSN candidate is the product page URL itself,
    // since the store doesn't expose a stable numeric id in the search markup.

    private static async Task<List<GameMetadataCandidate>> SafeSearchPsnAsync(string query, int max)
    {
        try { return await SearchPsnAsync(query, max); }
        catch { return new List<GameMetadataCandidate>(); }
    }

    private static async Task<List<GameMetadataCandidate>> SearchPsnAsync(string query, int max)
    {
        var url = $"https://store.playstation.com/search/{Uri.EscapeDataString(query)}";
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return new List<GameMetadataCandidate>();

        var html = await response.Content.ReadAsStringAsync();

        var results = new List<GameMetadataCandidate>();
        // Each result tile is an <li> inside the search results grid; grab each tile's
        // markup, then pull title/cover/link out of it independently (attribute order
        // in the store's markup isn't guaranteed, so each lookup is done separately
        // rather than assuming a fixed tag shape).
        foreach (Match li in Regex.Matches(html, @"<li[^>]*>(?<body>.*?)</li>", RegexOptions.Singleline))
        {
            var body = li.Groups["body"].Value;

            var title = Regex.Match(body, @"class=""[^""]*psw-t-body[^""]*""[^>]*>(?<title>[^<]+)<");
            var cover = Regex.Match(body,
                @"<img(?=[^>]*class=""[^""]*psw-l-fit-cover)(?=[^>]*src=""(?<src>[^""?]+))[^>]*>");
            var link = Regex.Match(body,
                @"<a(?=[^>]*class=""[^""]*psw-link)(?=[^>]*href=""(?<href>[^""]+))[^>]*>");

            if (!title.Success || !link.Success) continue;

            var name = WebUtility.HtmlDecode(title.Groups["title"].Value.Trim());
            var href = WebUtility.HtmlDecode(link.Groups["href"].Value);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(href)) continue;

            var gameUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : "https://store.playstation.com" + href;

            results.Add(new GameMetadataCandidate
            {
                Source = "PSN",
                Id = gameUrl,
                Name = name,
                ThumbnailUrl = cover.Success ? WebUtility.HtmlDecode(cover.Groups["src"].Value) : null
            });

            if (results.Count >= max) break;
        }
        return results;
    }

    private static async Task<GameMetadata?> FetchPsnDetailsAsync(string gameUrl)
    {
        using var response = await Http.GetAsync(gameUrl);
        if (!response.IsSuccessStatusCode) return null;

        var html = await response.Content.ReadAsStringAsync();

        var result = new GameMetadata { Platform = "PlayStation (PSN)" };

        // Covers on the store are square, same as the original plugin's approach of
        // reusing the square cover art for both cover and icon.
        var coverMatch = Regex.Match(html,
            @"<img(?=[^>]*class=""[^""]*psw-l-fit-cover)(?=[^>]*src=""(?<src>[^""?]+))[^>]*>");
        if (coverMatch.Success)
        {
            var coverUrl = WebUtility.HtmlDecode(coverMatch.Groups["src"].Value);
            var slug = Path.GetFileName(new Uri(gameUrl).AbsolutePath);
            result.CoverImagePath = await DownloadImageAsync(coverUrl, "psn", slug, "cover");
            result.BackgroundImagePath = await DownloadImageAsync(coverUrl, "psn", slug, "bg");
        }

        var descMatch = Regex.Match(html,
            @"<p(?=[^>]*class=""[^""]*psw-c-bg-card-1)[^>]*>(?<desc>.*?)</p>", RegexOptions.Singleline);
        if (descMatch.Success)
            result.Description = StripHtml(WebUtility.HtmlDecode(descMatch.Groups["desc"].Value));

        var titleMatch = Regex.Match(html, @"<h1[^>]*>(?<name>[^<]+)</h1>");
        if (titleMatch.Success)
            result.Name = WebUtility.HtmlDecode(titleMatch.Groups["name"].Value.Trim());

        return result;
    }

    // ---------------- shared ----------------

    private static string? NormalizeProtocolRelative(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.StartsWith("//") ? "https:" + url : url;

    private static string? StripHtml(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty).Trim();

    private static async Task<string?> DownloadImageAsync(string url, string source, string id, string tag)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".jpg";

            var path = Path.Combine(CacheFolder, $"{source}_{id}_{tag}{ext}");

            var bytes = await Http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
