using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    public ChannelService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
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
            try
            {
                return await LoadFromWebAsync();
            }
            catch
            {
                var cached = await LoadCacheAsync();
                if (cached.Count > 0)
                {
                    return new ChannelLoadResult(cached, false, GetCacheTimestamp());
                }
                throw;
            }
        }

        return await LoadFromWebAsync();
    }

    private async Task<ChannelLoadResult> LoadFromWebAsync()
    {
        using var response = await _httpClient.GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var markdown = await response.Content.ReadAsStringAsync();
        var channels = Parse(markdown);

        if (channels.Count == 0)
        {
            throw new InvalidOperationException("Η online λίστα δεν περιέχει ενεργά τηλεοπτικά κανάλια.");
        }

        await SaveCacheAsync(channels);
        return new ChannelLoadResult(channels, true, DateTimeOffset.Now);
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

            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out _))
            {
                continue;
            }

            if (IsYouTubeUrl(sourceUrl) || rawName.Contains('Ⓨ'))
            {
                continue;
            }

            var playbackUrl = GetCompatibilityPlaybackUrl(sourceUrl);

            channels.Add(new Channel
            {
                Name = CleanName(rawName),
                Url = playbackUrl,
                LogoUrl = logoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? logoUrl : string.Empty,
                EpgId = epgId,
                Category = currentCategory,
                GeoBlocked = rawName.Contains('Ⓖ'),
                IsYouTube = false
            });
        }

        return Sanitize(channels);
    }

    private static string GetCompatibilityPlaybackUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        // Free-TV currently lists the ERT family as DASH (.mpd). The same
        // Broadpeak/Siliconweb endpoints also expose HLS manifests. HLS is
        // substantially more reliable with the embedded LibVLC 3 player.
        if (url.EndsWith("/index.mpd", StringComparison.OrdinalIgnoreCase) &&
            (uri.Host.Equals("ert-live.siliconweb.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("ert-ucdn.broadpeak-aas.com", StringComparison.OrdinalIgnoreCase)))
        {
            return url[..^3] + "m3u8";
        }

        // Prefer TLS for SKAI when the source list still points to http.
        if (uri.Host.Equals("skai-live.siliconweb.com", StringComparison.OrdinalIgnoreCase) &&
            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + url["http://".Length..];
        }

        return url;
    }

    private async Task SaveCacheAsync(List<Channel> channels)
    {
        var json = JsonSerializer.Serialize(Sanitize(channels), new JsonSerializerOptions { WriteIndented = true });
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
            return Sanitize(cached.Select(channel => new Channel
            {
                Name = channel.Name,
                Url = GetCompatibilityPlaybackUrl(channel.Url),
                LogoUrl = channel.LogoUrl,
                EpgId = channel.EpgId,
                Category = channel.Category,
                GeoBlocked = channel.GeoBlocked,
                IsYouTube = channel.IsYouTube
            }));
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
