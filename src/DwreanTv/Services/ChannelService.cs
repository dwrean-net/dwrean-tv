using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DwreanTv.Models;

namespace DwreanTv.Services;

public sealed class ChannelService
{
    public const string SourceUrl = "https://raw.githubusercontent.com/Free-TV/IPTV/master/lists/greece.md";

    private const string EmbeddedSnapshotResource = "DwreanTv.default-greece.md";
    private const int ExpectedBuiltInChannelCount = 57;
    private const int MinimumHealthyChannelCount = 50;

    private static readonly string[] SourceUrls =
    {
        SourceUrl,
        "https://cdn.jsdelivr.net/gh/Free-TV/IPTV@master/lists/greece.md",
        "https://github.com/Free-TV/IPTV/raw/refs/heads/master/lists/greece.md"
    };

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
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dwrean-tv/0.2 (+https://www.dwrean.net/)");

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _cachePath = Path.Combine(dataDirectory, "channels-cache.json");
    }

    public async Task<ChannelLoadResult> LoadAsync(bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            var cached = await LoadCacheAsync();
            if (IsHealthy(cached))
            {
                _ = RefreshCacheSilentlyAsync();
                return new ChannelLoadResult(cached, false, GetCacheTimestamp());
            }

            // A clean first run must never depend on GitHub being reachable.
            // Show the complete embedded snapshot immediately and refresh it
            // silently in the background.
            var builtIn = LoadBuiltInSnapshot();
            await SaveCacheAsync(builtIn);
            _ = RefreshCacheSilentlyAsync();
            return new ChannelLoadResult(builtIn, false, null);
        }

        try
        {
            return await LoadFromWebAsync();
        }
        catch
        {
            var cached = await LoadCacheAsync();
            if (IsHealthy(cached))
            {
                return new ChannelLoadResult(cached, false, GetCacheTimestamp());
            }

            var builtIn = LoadBuiltInSnapshot();
            return new ChannelLoadResult(builtIn, false, null);
        }
    }

    private async Task RefreshCacheSilentlyAsync()
    {
        try
        {
            await LoadFromWebAsync();
        }
        catch
        {
            // The visible cached/embedded list is already complete and usable.
        }
    }

    private async Task<ChannelLoadResult> LoadFromWebAsync()
    {
        var attempts = SourceUrls
            .Select(FetchAndParseAsync)
            .ToList();

        Exception? lastError = null;

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);

            try
            {
                var channels = await finished;
                if (!IsHealthy(channels))
                {
                    continue;
                }

                await SaveCacheAsync(channels);
                return new ChannelLoadResult(channels, true, DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Δεν ήταν διαθέσιμη καμία πλήρης online πηγή καναλιών.");
    }

    private async Task<List<Channel>> FetchAndParseAsync(string url)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var markdown = await response.Content.ReadAsStringAsync();
        var channels = Parse(markdown);

        if (!IsHealthy(channels))
        {
            throw new InvalidOperationException($"Η online λίστα επέστρεψε μόνο {channels.Count} έγκυρα κανάλια.");
        }

        return channels;
    }

    private List<Channel> LoadBuiltInSnapshot()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedSnapshotResource)
            ?? throw new InvalidOperationException("Δεν βρέθηκε η ενσωματωμένη λίστα καναλιών.");
        using var reader = new StreamReader(stream);
        var channels = Parse(reader.ReadToEnd());

        if (channels.Count != ExpectedBuiltInChannelCount)
        {
            throw new InvalidOperationException(
                $"Η ενσωματωμένη λίστα καναλιών δεν είναι πλήρης ({channels.Count}/{ExpectedBuiltInChannelCount}).");
        }

        return channels;
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
            var url = WebUtility.HtmlDecode(rowMatch.Groups["url"].Value.Trim());
            var logoCell = rowMatch.Groups["logo"].Value;
            var epgId = WebUtility.HtmlDecode(rowMatch.Groups["epg"].Value.Trim());
            var logoMatch = LogoRegex.Match(logoCell);
            var logoUrl = logoMatch.Success
                ? WebUtility.HtmlDecode(logoMatch.Groups["url"].Value.Trim())
                : string.Empty;

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                continue;
            }

            if (IsYouTubeUrl(url) || rawName.Contains('Ⓨ'))
            {
                continue;
            }

            channels.Add(new Channel
            {
                Name = CleanName(rawName),
                Url = url,
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
        var sanitized = Sanitize(channels);
        if (!IsHealthy(sanitized))
        {
            return;
        }

        var json = JsonSerializer.Serialize(sanitized, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_cachePath, json);
    }

    private async Task<List<Channel>> LoadCacheAsync()
    {
        if (!File.Exists(_cachePath))
        {
            return new List<Channel>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_cachePath);
            var cached = JsonSerializer.Deserialize<List<Channel>>(json) ?? new List<Channel>();
            var sanitized = Sanitize(cached);

            // Invalidates the old 30-channel emergency cache automatically.
            return IsHealthy(sanitized) ? sanitized : new List<Channel>();
        }
        catch
        {
            return new List<Channel>();
        }
    }

    private DateTimeOffset? GetCacheTimestamp()
    {
        if (!File.Exists(_cachePath))
        {
            return null;
        }

        return File.GetLastWriteTime(_cachePath);
    }

    private static bool IsHealthy(IReadOnlyCollection<Channel> channels)
    {
        return channels.Count >= MinimumHealthyChannelCount;
    }

    private static List<Channel> Sanitize(IEnumerable<Channel> channels)
    {
        return channels
            .Where(c => !c.IsYouTube)
            .Where(c => !IsYouTubeUrl(c.Url))
            .Where(c => !c.Category.Contains("Radio", StringComparison.OrdinalIgnoreCase))
            .Where(c => !c.Category.Contains("Ραδιό", StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                c => string.IsNullOrWhiteSpace(c.EpgId) ? $"{c.Name}|{c.Url}" : c.EpgId,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static bool IsYouTubeUrl(string url)
    {
        return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanName(string value)
    {
        return value
            .Replace("Ⓖ", string.Empty)
            .Replace("Ⓨ", string.Empty)
            .Replace("Ⓢ", string.Empty)
            .Trim();
    }

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

public sealed record ChannelLoadResult(IReadOnlyList<Channel> Channels, bool FromWeb, DateTimeOffset? UpdatedAt);
