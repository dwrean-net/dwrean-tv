using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DwreanTv.Models;

namespace DwreanTv.Services;

public sealed class ChannelService
{
    public const string SourceUrl = "https://raw.githubusercontent.com/Free-TV/IPTV/master/lists/greece.md";

    private static readonly (string Url, bool GitHubApi, string Name)[] Sources =
    [
        ("https://raw.githubusercontent.com/Free-TV/IPTV/master/lists/greece.md", false, "GitHub Raw"),
        ("https://cdn.jsdelivr.net/gh/Free-TV/IPTV@master/lists/greece.md", false, "jsDelivr CDN"),
        ("https://api.github.com/repos/Free-TV/IPTV/contents/lists/greece.md?ref=master", true, "GitHub API")
    ];

    private static readonly Regex HeadingRegex = new(
        @"<h2>(?<title>.*?)</h2>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RowRegex = new(
        @"^\|\s*\d+\s*\|\s*(?<name>.*?)\s*\|\s*\[>\]\((?<url>.*?)\)\s*\|\s*(?<logo>.*?)\s*\|\s*(?<epg>.*?)\s*\|",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LogoRegex = new(
        "src=\\\"(?<url>[^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly string _cachePath;

    public ChannelService()
    {
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dwrean-tv/0.2.3-test (+https://www.dwrean.net/)");

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _cachePath = Path.Combine(dataDirectory, "channels-cache.json");
    }

    public async Task<ChannelLoadResult> LoadAsync(bool forceRefresh = false)
    {
        var online = await TryLoadFromWebAsync();
        if (online is not null)
        {
            return online;
        }

        var cached = await LoadCacheAsync();
        if (cached.Count > 0)
        {
            return new ChannelLoadResult(cached, false, GetCacheTimestamp(), "από το τελευταίο αποθηκευμένο αντίγραφο");
        }

        throw new InvalidOperationException("Δεν ήταν δυνατή η λήψη της λίστας καναλιών και δεν υπάρχει αποθηκευμένο αντίγραφο.");
    }

    private async Task<ChannelLoadResult?> TryLoadFromWebAsync()
    {
        using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = Sources.Select(source => FetchAndParseSourceAsync(source, overallCts.Token)).ToList();

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            try
            {
                var result = await completed;
                if (result.Channels.Count > 0)
                {
                    overallCts.Cancel();
                    await SaveCacheAsync(result.Channels);
                    return new ChannelLoadResult(result.Channels, true, DateTimeOffset.Now, $"online μέσω {result.SourceName}");
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private async Task<SourceLoadResult> FetchAndParseSourceAsync(
        (string Url, bool GitHubApi, string Name) source,
        CancellationToken overallToken)
    {
        using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
        sourceCts.CancelAfter(TimeSpan.FromSeconds(12));

        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        request.Headers.Accept.ParseAdd(source.GitHubApi ? "application/vnd.github+json" : "text/plain, text/markdown, */*");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sourceCts.Token);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(sourceCts.Token);
        var markdown = source.GitHubApi ? DecodeGitHubApiContent(payload) : payload;
        return new SourceLoadResult(Parse(markdown), source.Name);
    }

    private static string DecodeGitHubApiContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("content", out var contentElement))
        {
            throw new InvalidOperationException("Η απάντηση του GitHub API δεν περιέχει τη λίστα.");
        }

        var content = (contentElement.GetString() ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        return Encoding.UTF8.GetString(Convert.FromBase64String(content));
    }

    public List<Channel> Parse(string markdown)
    {
        var channels = new List<Channel>();
        var currentCategory = "Άλλα κανάλια";
        var skipCurrentCategory = false;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();
            var headingMatch = HeadingRegex.Match(line);

            if (headingMatch.Success)
            {
                var heading = WebUtility.HtmlDecode(headingMatch.Groups["title"].Value.Trim());
                skipCurrentCategory = heading.Contains("Radio", StringComparison.OrdinalIgnoreCase);
                currentCategory = TranslateCategory(heading);
                continue;
            }

            if (skipCurrentCategory)
            {
                continue;
            }

            var rowMatch = RowRegex.Match(line);
            if (!rowMatch.Success)
            {
                continue;
            }

            var rawName = WebUtility.HtmlDecode(rowMatch.Groups["name"].Value.Trim());
            var sourceUrl = WebUtility.HtmlDecode(rowMatch.Groups["url"].Value.Trim());
            var logoCell = rowMatch.Groups["logo"].Value;
            var epgId = WebUtility.HtmlDecode(rowMatch.Groups["epg"].Value.Trim());
            var logoMatch = LogoRegex.Match(logoCell);
            var logoUrl = logoMatch.Success ? WebUtility.HtmlDecode(logoMatch.Groups["url"].Value.Trim()) : string.Empty;

            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out _) || IsYouTubeUrl(sourceUrl) || rawName.Contains('Ⓨ'))
            {
                continue;
            }

            channels.Add(new Channel
            {
                Name = CleanName(rawName),
                Url = sourceUrl,
                LogoUrl = logoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? logoUrl : string.Empty,
                EpgId = epgId,
                Category = currentCategory,
                GeoBlocked = rawName.Contains('Ⓖ'),
                IsYouTube = false
            });
        }

        return Sanitize(channels);
    }

    private async Task SaveCacheAsync(List<Channel> channels)
    {
        try
        {
            var json = JsonSerializer.Serialize(Sanitize(channels), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_cachePath, json);
        }
        catch
        {
        }
    }

    private async Task<List<Channel>> LoadCacheAsync()
    {
        if (!File.Exists(_cachePath)) return [];

        try
        {
            var json = await File.ReadAllTextAsync(_cachePath);
            return Sanitize(JsonSerializer.Deserialize<List<Channel>>(json) ?? []);
        }
        catch
        {
            return [];
        }
    }

    private DateTimeOffset? GetCacheTimestamp() => File.Exists(_cachePath) ? File.GetLastWriteTime(_cachePath) : null;

    private static List<Channel> Sanitize(IEnumerable<Channel> channels)
    {
        return channels
            .Where(c => !c.IsYouTube)
            .Where(c => !IsYouTubeUrl(c.Url))
            .Where(c => !c.Category.Contains("Radio", StringComparison.OrdinalIgnoreCase))
            .Where(c => !c.Category.Contains("Ραδιό", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => string.IsNullOrWhiteSpace(c.EpgId) ? $"{c.Name}|{c.Url}" : c.EpgId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static bool IsYouTubeUrl(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    private static string CleanName(string value) => value
        .Replace("Ⓖ", string.Empty)
        .Replace("Ⓨ", string.Empty)
        .Replace("Ⓢ", string.Empty)
        .Trim();

    private static string TranslateCategory(string category)
    {
        if (category.Contains("Public", StringComparison.OrdinalIgnoreCase)) return "Δημόσια";
        if (category.Contains("Private National", StringComparison.OrdinalIgnoreCase)) return "Πανελλαδικά";
        if (category.Contains("Athens", StringComparison.OrdinalIgnoreCase)) return "Αθήνα / Αττική";
        if (category.Contains("Thessaloniki", StringComparison.OrdinalIgnoreCase)) return "Θεσσαλονίκη / Κ. Μακεδονία";
        if (category.Contains("Peloponnese", StringComparison.OrdinalIgnoreCase)) return "Πελοπόννησος";
        if (category.Contains("Eastern Sterea", StringComparison.OrdinalIgnoreCase)) return "Στερεά / Εύβοια";
        if (category.Contains("Thessaly", StringComparison.OrdinalIgnoreCase)) return "Θεσσαλία";
        if (category.Contains("Epirus", StringComparison.OrdinalIgnoreCase)) return "Ήπειρος";
        if (category.Contains("Crete", StringComparison.OrdinalIgnoreCase)) return "Κρήτη";
        if (category.Contains("Aegean", StringComparison.OrdinalIgnoreCase)) return "Αιγαίο";
        if (category.Contains("Ionian", StringComparison.OrdinalIgnoreCase)) return "Ιόνιο";
        if (category.Contains("Macedonia", StringComparison.OrdinalIgnoreCase)) return "Μακεδονία";
        if (category.Contains("Thrace", StringComparison.OrdinalIgnoreCase)) return "Θράκη";
        return category;
    }
}

public sealed record ChannelLoadResult(
    IReadOnlyList<Channel> Channels,
    bool FromWeb,
    DateTimeOffset? UpdatedAt,
    string SourceDescription = "online");

internal sealed record SourceLoadResult(List<Channel> Channels, string SourceName);
