using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using DwreanTv.Models;

namespace DwreanTv.Services;

public sealed class ChannelService
{
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
    private readonly string _bundledListPath;

    public ChannelService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dwrean-tv/0.3.1");

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _cachePath = Path.Combine(dataDirectory, "channels-cache-verbatim.json");
        _bundledListPath = Path.Combine(dataDirectory, "greece.md");
    }

    public async Task<ChannelLoadResult> LoadAsync(bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            var bundled = await LoadBundledListAsync();
            if (bundled.Count > 0)
            {
                return new ChannelLoadResult(
                    bundled,
                    false,
                    File.GetLastWriteTime(_bundledListPath),
                    "ενσωματωμένη λίστα Free-TV");
            }
        }

        var online = await TryLoadFromWebAsync();
        if (online is not null)
        {
            return online;
        }

        var fallbackBundled = await LoadBundledListAsync();
        if (fallbackBundled.Count > 0)
        {
            return new ChannelLoadResult(
                fallbackBundled,
                false,
                File.GetLastWriteTime(_bundledListPath),
                "ενσωματωμένη λίστα Free-TV");
        }

        var cached = await LoadCacheAsync();
        if (cached.Count > 0)
        {
            return new ChannelLoadResult(
                cached,
                false,
                File.GetLastWriteTime(_cachePath),
                "τελευταίο αποθηκευμένο αντίγραφο");
        }

        throw new InvalidOperationException("Δεν βρέθηκε η ενσωματωμένη λίστα Free-TV.");
    }

    private async Task<ChannelLoadResult?> TryLoadFromWebAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync(SourceUrl);
            response.EnsureSuccessStatusCode();
            var markdown = await response.Content.ReadAsStringAsync();
            var channels = Parse(markdown);

            if (channels.Count == 0)
            {
                return null;
            }

            await SaveCacheAsync(channels);
            return new ChannelLoadResult(
                channels,
                true,
                DateTimeOffset.Now,
                "Free-TV online");
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<Channel>> LoadBundledListAsync()
    {
        if (!File.Exists(_bundledListPath))
        {
            return [];
        }

        try
        {
            var markdown = await File.ReadAllTextAsync(_bundledListPath);
            return Parse(markdown);
        }
        catch
        {
            return [];
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

            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out _) ||
                IsYouTubeUrl(sourceUrl) ||
                rawName.Contains('Ⓨ'))
            {
                continue;
            }

            channels.Add(new Channel
            {
                Name = CleanName(rawName),
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

        return channels
            .GroupBy(c => $"{c.Name}\n{c.Url}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private async Task SaveCacheAsync(List<Channel> channels)
    {
        try
        {
            var json = JsonSerializer.Serialize(channels, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_cachePath, json);
        }
        catch
        {
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
            return JsonSerializer.Deserialize<List<Channel>>(json) ?? [];
        }
        catch
        {
            return [];
        }
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
        if (category.Contains("Thessalia", StringComparison.OrdinalIgnoreCase)) return "Θεσσαλία";
        if (category.Contains("Western Greece", StringComparison.OrdinalIgnoreCase)) return "Δυτική Ελλάδα";
        if (category.Contains("Thrace", StringComparison.OrdinalIgnoreCase)) return "Θράκη / Αν. Μακεδονία";
        if (category.Contains("Crete", StringComparison.OrdinalIgnoreCase)) return "Κρήτη";
        if (category.Contains("Aegean", StringComparison.OrdinalIgnoreCase)) return "Αιγαίο";
        return category;
    }
}

public sealed record ChannelLoadResult(
    IReadOnlyList<Channel> Channels,
    bool FromWeb,
    DateTimeOffset? UpdatedAt,
    string SourceDescription = "online");