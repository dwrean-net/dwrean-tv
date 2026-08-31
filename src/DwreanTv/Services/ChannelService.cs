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
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) dwrean-tv/0.2.1 (+https://www.dwrean.net/)");

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
            return new ChannelLoadResult(
                cached,
                false,
                GetCacheTimestamp(),
                "από το τελευταίο αποθηκευμένο αντίγραφο");
        }

        var emergency = GetEmergencyChannels();
        return new ChannelLoadResult(
            emergency,
            false,
            null,
            "ενσωματωμένη εφεδρική λίστα");
    }

    private async Task<ChannelLoadResult?> TryLoadFromWebAsync()
    {
        using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        var pending = Sources
            .Select(source => FetchAndParseSourceAsync(source, overallCts.Token))
            .ToList();

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
                    return new ChannelLoadResult(
                        result.Channels,
                        true,
                        DateTimeOffset.Now,
                        $"online μέσω {result.SourceName}");
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
        sourceCts.CancelAfter(TimeSpan.FromSeconds(9));

        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        request.Headers.Accept.ParseAdd(source.GitHubApi
            ? "application/vnd.github+json"
            : "text/plain, text/markdown, */*");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            sourceCts.Token);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(sourceCts.Token);
        var markdown = source.GitHubApi ? DecodeGitHubApiContent(payload) : payload;
        var channels = Parse(markdown);
        return new SourceLoadResult(channels, source.Name);
    }

    private static string DecodeGitHubApiContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("content", out var contentElement))
        {
            throw new InvalidOperationException("Η απάντηση του GitHub API δεν περιέχει τη λίστα.");
        }

        var content = contentElement.GetString() ?? string.Empty;
        content = content.Replace("\r", string.Empty).Replace("\n", string.Empty);

        if (content.Length == 0)
        {
            throw new InvalidOperationException("Η λίστα του GitHub API είναι κενή.");
        }

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
            var logoUrl = logoMatch.Success
                ? WebUtility.HtmlDecode(logoMatch.Groups["url"].Value.Trim())
                : string.Empty;

            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out _))
            {
                continue;
            }

            if (IsYouTubeUrl(sourceUrl) || rawName.Contains('Ⓨ'))
            {
                continue;
            }

            channels.Add(new Channel
            {
                Name = CleanName(rawName),
                Url = GetPreferredPlaybackUrl(epgId, sourceUrl),
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

    private static string GetPreferredPlaybackUrl(string epgId, string sourceUrl)
    {
        return epgId.Trim() switch
        {
            "ERT1.gr" => "https://ertflix.s.llnwi.net/ertlive/ert1/default/index.m3u8",
            "ERT2.gr" => "https://ertflix.s.llnwi.net/ertlive/ert2/default/index.m3u8",
            "ERT3.gr" => "https://ertflix.s.llnwi.net/ertlive/ert3/default/index.m3u8",
            "ERTNews.gr" => "https://ertflix.s.llnwi.net/ertlive/ertnews/default/index.m3u8",
            _ => sourceUrl
        };
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
            var cached = JsonSerializer.Deserialize<List<Channel>>(json) ?? [];
            return Sanitize(cached);
        }
        catch
        {
            return [];
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

    private static List<Channel> GetEmergencyChannels()
    {
        return Sanitize(
        [
            C("ERT 1", "https://ertflix.s.llnwi.net/ertlive/ert1/default/index.m3u8", "https://i.imgur.com/WWMe8IY.png", "ERT1.gr", "Δημόσια", true),
            C("ERT 2", "https://ertflix.s.llnwi.net/ertlive/ert2/default/index.m3u8", "https://i.imgur.com/pcusPFl.png", "ERT2.gr", "Δημόσια", true),
            C("ERT 3", "https://ertflix.s.llnwi.net/ertlive/ert3/default/index.m3u8", "https://i.imgur.com/KyhzDRm.png", "ERT3.gr", "Δημόσια", true),
            C("ERT News", "https://ertflix.s.llnwi.net/ertlive/ertnews/default/index.m3u8", "https://i.imgur.com/saIGLvr.png", "ERTNews.gr", "Δημόσια"),
            C("ERT Cosmos", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTCosmos/default/index.mpd", "https://i.imgur.com/KsMTWYw.png", "ERTWorld.gr", "Δημόσια"),
            C("ERT Sports 1", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTSports1/default/index.mpd", "https://i.imgur.com/gebWmAB.png", "ERTSports1.gr", "Δημόσια"),
            C("ERT Sports 2", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTSports2/default/index.mpd", "https://i.imgur.com/gebWmAB.png", "ERTSports2.gr", "Δημόσια"),
            C("ERT Sports 3", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTSports3/default/index.mpd", "https://i.imgur.com/gebWmAB.png", "ERTSports3.gr", "Δημόσια"),
            C("ERT Sports 4", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTSports4/default/index.mpd", "https://i.imgur.com/gebWmAB.png", "ERTSports4.gr", "Δημόσια"),
            C("ERT Kids", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTKids/default/index.mpd", "https://i.imgur.com/XkSR66q.png", "ERTKids.gr", "Δημόσια", true),
            C("Vouli TV", "https://diavlos-cache.cnt.grnet.gr/parltv/webtv-1b.sdp/playlist.m3u8", "https://i.imgur.com/1vqW7lc.png", "VouliTV.gr", "Δημόσια"),
            C("RIK Sat", "https://l3.cloudskep.com/cybcsat/abr/playlist.m3u8", "https://i.imgur.com/9edlXHP.png", "RikSatTV.cy", "Δημόσια"),
            C("Mega News", "https://c98db5952cb54b358365984178fb898a.msvdn.net/live/S99841657/NU0xOarAMJ5X/playlist.m3u8", "https://i.imgur.com/Z3k7iA0.png", "MegaChannel.gr", "Πανελλαδικά"),
            C("ANT1", "https://mcdn.antennaplus.gr/live/media0/Ant1/HLS/Ant1.m3u8", "https://i.imgur.com/xDdVa9U.png", "ANT1.gr", "Πανελλαδικά"),
            C("Star", "https://livestar.siliconweb.com/starvod/star4/star4.m3u8", "https://i.imgur.com/Hp0stVQ.png", "StarChannel.gr", "Πανελλαδικά"),
            C("AlphaTV", "https://alphatvlive2.siliconweb.com/alphatvlive/live_abr/playlist.m3u8", "https://i.imgur.com/bAVGX0l.png", "AlphaTV.gr", "Πανελλαδικά"),
            C("Skai TV", "http://skai-live.siliconweb.com/media/cambria4/index.m3u8", "https://i.imgur.com/TSg7B8X.png", "SkaiTV.gr", "Πανελλαδικά"),
            C("Open TV", "https://liveopen.siliconweb.com/openTvLive/liveopen/playlist.m3u8", "https://i.imgur.com/HzBmvPT.png", "OpenTV.gr", "Πανελλαδικά"),
            C("MAK TV", "https://mcdn.antennaplus.gr/live/media0/MAK/HLS/MAK.m3u8", "https://i.imgur.com/90iDHbQ.png", "MakTV.gr", "Πανελλαδικά")
        ]);
    }

    private static Channel C(
        string name,
        string url,
        string logo,
        string epg,
        string category,
        bool geo = false)
    {
        return new Channel
        {
            Name = name,
            Url = url,
            LogoUrl = logo,
            EpgId = epg,
            Category = category,
            GeoBlocked = geo,
            IsYouTube = false
        };
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
        if (category.Contains("Thessalia", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("Thessaly", StringComparison.OrdinalIgnoreCase)) return "Θεσσαλία";
        if (category.Contains("Epirus", StringComparison.OrdinalIgnoreCase)) return "Ήπειρος";
        if (category.Contains("Crete", StringComparison.OrdinalIgnoreCase)) return "Κρήτη";
        if (category.Contains("Aegean", StringComparison.OrdinalIgnoreCase)) return "Αιγαίο";
        if (category.Contains("Ionian", StringComparison.OrdinalIgnoreCase)) return "Ιόνιο";
        if (category.Contains("Macedonia", StringComparison.OrdinalIgnoreCase)) return "Μακεδονία";
        if (category.Contains("Thrace", StringComparison.OrdinalIgnoreCase)) return "Θράκη";
        return category;
    }

    private sealed record SourceLoadResult(List<Channel> Channels, string SourceName);
}

public sealed record ChannelLoadResult(
    IReadOnlyList<Channel> Channels,
    bool FromWeb,
    DateTimeOffset? UpdatedAt,
    string SourceDescription);
