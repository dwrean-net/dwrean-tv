using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DwreanTv.Models;

namespace DwreanTv.Services;

public sealed class ChannelService
{
    public const string SourceUrl = "https://raw.githubusercontent.com/Free-TV/IPTV/master/lists/greece.md";

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
            // All mirrors are tried in parallel, so startup will never wait
            // for three consecutive timeouts.
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dwrean-tv/0.2 (+https://www.dwrean.net/)");

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _cachePath = Path.Combine(dataDirectory, "channels-cache.json");
    }

    public async Task<ChannelLoadResult> LoadAsync(bool forceRefresh = false)
    {
        // Normal startup: if we already have a valid cached list, show it
        // immediately. Refresh it silently in the background so a slow
        // GitHub connection can never leave the app empty.
        if (!forceRefresh)
        {
            var cached = await LoadCacheAsync();
            if (cached.Count > 0)
            {
                _ = RefreshCacheSilentlyAsync();
                return new ChannelLoadResult(cached, false, GetCacheTimestamp());
            }
        }

        try
        {
            return await LoadFromWebAsync();
        }
        catch
        {
            // On manual refresh, preserve the last known-good list if the
            // network or one of the hosting services is temporarily down.
            var cached = await LoadCacheAsync();
            if (cached.Count > 0)
            {
                return new ChannelLoadResult(cached, false, GetCacheTimestamp());
            }

            // First run with no cache at all: never show an empty app. This
            // built-in emergency list contains the main Greek channels and
            // is used only until an online source becomes available.
            var fallback = GetBuiltInFallbackChannels();
            return new ChannelLoadResult(fallback, false, null);
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
            // The visible cached list is already usable. A background refresh
            // failure must never interrupt the user with a dialog.
        }
    }

    private async Task<ChannelLoadResult> LoadFromWebAsync()
    {
        var attempts = SourceUrls
            .Select(url => FetchAndParseAsync(url))
            .ToList();

        Exception? lastError = null;

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);

            try
            {
                var channels = await finished;
                if (channels.Count == 0)
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

        throw lastError ?? new InvalidOperationException("Δεν ήταν διαθέσιμη καμία online πηγή καναλιών.");
    }

    private async Task<List<Channel>> FetchAndParseAsync(string url)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var markdown = await response.Content.ReadAsStringAsync();
        var channels = Parse(markdown);

        if (channels.Count == 0)
        {
            throw new InvalidOperationException("Η online λίστα δεν περιέχει ενεργά τηλεοπτικά κανάλια.");
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
            var logoUrl = logoMatch.Success ? WebUtility.HtmlDecode(logoMatch.Groups["url"].Value.Trim()) : string.Empty;

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
            return Sanitize(cached);
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

    private static List<Channel> GetBuiltInFallbackChannels()
    {
        return Sanitize(new[]
        {
            Ch("ERT 1", "https://ert-live.siliconweb.com/bpk-tv/ERT1/default/index.mpd", "https://i.imgur.com/WWMe8IY.png", "ERT1.gr", "Δημόσια", true),
            Ch("ERT 2", "https://ert-live.siliconweb.com/bpk-tv/ERT2/default/index.mpd", "https://i.imgur.com/pcusPFl.png", "ERT2.gr", "Δημόσια", true),
            Ch("ERT 3", "https://ert-live.siliconweb.com/bpk-tv/ERT3/default/index.mpd", "https://i.imgur.com/KyhzDRm.png", "ERT3.gr", "Δημόσια", true),
            Ch("ERT News", "https://ert-live.siliconweb.com/bpk-tv/ERTNews/default/index.mpd", "https://i.imgur.com/saIGLvr.png", "ERTNews.gr", "Δημόσια"),
            Ch("ERT World", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTWorld/default/index.mpd", "https://i.imgur.com/KsMTWYw.png", "ERTWorld.gr", "Δημόσια"),
            Ch("ERT Sports 1", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTSports1/default/index.mpd", "https://i.imgur.com/gebWmAB.png", "ERTSports1.gr", "Δημόσια"),
            Ch("ERT Sports 2", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTSports2/default/index.mpd", "https://i.imgur.com/gebWmAB.png", "ERTSports2.gr", "Δημόσια"),
            Ch("ERT Sports 3", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTSports3/default/index.mpd", "https://i.imgur.com/gebWmAB.png", "ERTSports3.gr", "Δημόσια"),
            Ch("ERT Sports 4", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTSports4/default/index.mpd", "https://i.imgur.com/gebWmAB.png", "ERTSports4.gr", "Δημόσια"),
            Ch("ERT Kids", "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTKids/default/index.mpd", "https://i.imgur.com/XkSR66q.png", "ERTKids.gr", "Δημόσια", true),
            Ch("Vouli TV", "https://diavlos-cache.cnt.grnet.gr/parltv/webtv-1b.sdp/playlist.m3u8", "https://i.imgur.com/1vqW7lc.png", "VouliTV.gr", "Δημόσια"),
            Ch("RIK Sat", "https://l3.cloudskep.com/cybcsat/abr/playlist.m3u8", "https://i.imgur.com/9edlXHP.png", "RikSatTV.cy", "Δημόσια"),
            Ch("Mega News", "https://c98db5952cb54b358365984178fb898a.msvdn.net/live/S99841657/NU0xOarAMJ5X/playlist.m3u8", "https://i.imgur.com/Z3k7iA0.png", "MegaChannel.gr", "Πανελλαδικά"),
            Ch("ANT1", "https://mcdn.antennaplus.gr/live/media0/Ant1/HLS/Ant1.m3u8", "https://i.imgur.com/xDdVa9U.png", "ANT1.gr", "Πανελλαδικά"),
            Ch("Star", "https://livestar.siliconweb.com/starvod/star4/star4.m3u8", "https://i.imgur.com/Hp0stVQ.png", "StarChannel.gr", "Πανελλαδικά"),
            Ch("AlphaTV", "https://alphatvlive2.siliconweb.com/alphatvlive/live_abr/playlist.m3u8", "https://i.imgur.com/bAVGX0l.png", "AlphaTV.gr", "Πανελλαδικά"),
            Ch("Skai TV", "http://skai-live.siliconweb.com/media/cambria4/index.m3u8", "https://i.imgur.com/TSg7B8X.png", "SkaiTV.gr", "Πανελλαδικά"),
            Ch("Open TV", "https://liveopen.siliconweb.com/openTvLive/liveopen/playlist.m3u8", "https://i.imgur.com/HzBmvPT.png", "OpenTV.gr", "Πανελλαδικά"),
            Ch("MAK TV", "https://mcdn.antennaplus.gr/live/media0/MAK/HLS/MAK.m3u8", "https://i.imgur.com/90iDHbQ.png", "MakTV.gr", "Πανελλαδικά"),
            Ch("Action24", "https://actionlive.siliconweb.com/actionabr/actiontv/playlist.m3u8", "https://i.imgur.com/Zi1YohT.png", "Action24TV.gr", "Αθήνα / Αττική"),
            Ch("High TV", "https://live.streams.ovh/hightv/hightv/playlist.m3u8", "https://i.imgur.com/wHzCGry.png", "hightv.gr", "Αθήνα / Αττική"),
            Ch("Kontra", "https://kontralive.siliconweb.com/live/kontratv/playlist.m3u8", "https://i.imgur.com/ROZ9VfV.png", "KontraChannel.gr", "Αθήνα / Αττική"),
            Ch("Naftemporiki TV", "https://telmaco.ascdn.broadpeak.io/nafteboriki/default/index.m3u8", "https://i.imgur.com/9OFdMud.png", "NaftemporikiTV.gr", "Αθήνα / Αττική"),
            Ch("One Channel", "https://onechannel.siliconweb.com/one/stream/chunks_dvr.m3u8", "https://i.imgur.com/GwKaHbM.png", "OneChannel.gr", "Αθήνα / Αττική"),
            Ch("4E", "http://eu2.tv4e.gr:1935/live/myStream.sdp/playlist.m3u8", "https://i.imgur.com/Ed085oJ.png", "4E.gr", "Θεσσαλονίκη / Κ. Μακεδονία"),
            Ch("Egnatia", "https://video.streams.ovh:1936/egnatiatv/egnatiatv/index.m3u", "https://i.imgur.com/zuyYIca.png", "egnatiatv.gr", "Θεσσαλονίκη / Κ. Μακεδονία"),
            Ch("TV 100", "https://panel.gwebstream.eu:19360/tv100skg/tv100skg.m3u8", "https://i.imgur.com/9rtf8OR.png", "TV100.gr", "Θεσσαλονίκη / Κ. Μακεδονία"),
            Ch("Vergina", "https://verginanews.gr:8443/hls_live/stream1.m3u8", "https://i.imgur.com/cpF6wvR.png", "verginatv.gr", "Θεσσαλονίκη / Κ. Μακεδονία"),
            Ch("Best TV", "https://besttv.siliconweb.com/bestTV/live_abr/playlist.m3u8", "https://i.imgur.com/VA13E3w.png", "besttv.gr", "Πελοπόννησος"),
            Ch("Ionian Channel", "https://stream.ioniantv.gr/ionian/live_abr/playlist.m3u8", "https://i.imgur.com/ADVYeQd.png", "ioniantv.gr", "Πελοπόννησος")
        });
    }

    private static Channel Ch(
        string name,
        string url,
        string logoUrl,
        string epgId,
        string category,
        bool geoBlocked = false)
    {
        return new Channel
        {
            Name = name,
            Url = url,
            LogoUrl = logoUrl,
            EpgId = epgId,
            Category = category,
            GeoBlocked = geoBlocked,
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
