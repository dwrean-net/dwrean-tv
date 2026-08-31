using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DwreanTv.Models;

namespace DwreanTv.Services;

public sealed class ChannelService
{
    // Canonical source requested for the app. The GitHub page provided by the project
    // corresponds to this raw file; channel playback URLs are copied verbatim from it.
    public const string SourceUrl = "https://raw.githubusercontent.com/Free-TV/IPTV/master/lists/greece.md";

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
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // This header is used only to download the markdown list from GitHub.
        // It is never applied to channel playback.
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dwrean-tv/0.2.3-test");

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);

        // New cache name deliberately prevents older builds from restoring URLs
        // that had previously been modified by compatibility experiments.
        _cachePath = Path.Combine(dataDirectory, "channels-cache-free-tv-original-v1.json");
    }

    public async Task<ChannelLoadResult> LoadAsync(bool forceRefresh = false)
    {
        try
        {
            var markdown = await _httpClient.GetStringAsync(SourceUrl);
            var channels = Parse(markdown);

            if (channels.Count == 0)
            {
                throw new InvalidOperationException("Η λίστα Free-TV δεν επέστρεψε ενεργά τηλεοπτικά κανάλια.");
            }

            await SaveCacheAsync(channels);
            return new ChannelLoadResult(
                channels,
                true,
                DateTimeOffset.Now,
                "Free-TV / IPTV – Greece");
        }
        catch
        {
            var cached = await LoadCacheAsync();
            if (cached.Count > 0)
            {
                return new ChannelLoadResult(
                    cached,
                    false,
                    GetCacheTimestamp(),
                    "τελευταίο αντίγραφο της Free-TV Greece");
            }

            throw new InvalidOperationException(
                "Δεν ήταν δυνατή η λήψη της λίστας Free-TV Greece και δεν υπάρχει αποθηκευμένο αντίγραφο.");
        }
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

            // Only [>] rows from the original Free-TV list are considered active.
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
            var logoUrl = logoMatch.Success
                ? WebUtility.HtmlDecode(logoMatch.Groups["url"].Value.Trim())
                : string.Empty;

            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out _))
            {
                continue;
            }

            // Previous product decision: TV only, no YouTube-based channels.
            if (IsYouTubeUrl(sourceUrl) || rawName.Contains('Ⓨ'))
            {
                continue;
            }

            channels.Add(new Channel
            {
                Name = CleanName(rawName),
                // IMPORTANT: exact URL from Free-TV. No rewriting, normalization,
                // fallback substitution or CDN replacement is performed here.
                Url = sourceUrl,
                LogoUrl = logoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? logoUrl
                    : string.Empty,
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
            var json = JsonSerializer.Serialize(
                Sanitize(channels),
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_cachePath, json);
        }
        catch
        {
            // Cache failure must not prevent online playback.
        }
    }

    private async Task<List<Channel>> LoadCacheAsync()
    {
        if (!File.Exists(_cachePath))
        {
            return [];
        }

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

    private DateTimeOffset? GetCacheTimestamp() =>
        File.Exists(_cachePath) ? File.GetLastWriteTime(_cachePath) : null;

    private static List<Channel> Sanitize(IEnumerable<Channel> channels)
    {
        return channels
            .Where(c => !c.IsYouTube)
            .Where(c => !IsYouTubeUrl(c.Url))
            .Where(c => !c.Category.Contains("Radio", StringComparison.OrdinalIgnoreCase))
            .Where(c => !c.Category.Contains("Ραδιό", StringComparison.OrdinalIgnoreCase))
            // Preserve separate rows from Free-TV even when they share the same EPG id.
            .GroupBy(c => $"{c.Name}|{c.Url}", StringComparer.OrdinalIgnoreCase)
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
