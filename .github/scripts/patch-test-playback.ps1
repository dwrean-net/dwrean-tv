$path = 'src/DwreanTv/MainForm.cs'
$text = Get-Content -LiteralPath $path -Raw

# Add a generation counter so an old failed stream cannot interfere
# after the user selects another channel.
$text = [regex]::Replace(
    $text,
    '(?m)^    private Channel\? _currentChannel;\r?\n',
    "    private Channel? _currentChannel;`n    private int _playbackGeneration;`n",
    1)

$startMarker = '    private void PlayChannel(Channel channel)'
$endMarker = '    private bool IsFavorite(Channel channel)'
$start = $text.IndexOf($startMarker, [StringComparison]::Ordinal)
$end = $text.IndexOf($endMarker, $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -lt 0) {
    throw 'Could not locate PlayChannel block in MainForm.cs'
}

$replacement = @'
    private async void PlayChannel(Channel channel)
    {
        _currentChannel = channel;
        var generation = ++_playbackGeneration;
        _settings.LastChannelKey = SettingsService.GetChannelKey(channel);
        _settingsService.Save(_settings);
        UpdateCurrentFavoriteButton();
        RefreshChannelCards();

        _nowPlayingLabel.Text = channel.Name;
        var candidates = GetPlaybackCandidates(channel);

        for (var index = 0; index < candidates.Count; index++)
        {
            if (generation != _playbackGeneration || _currentChannel != channel)
            {
                return;
            }

            var candidate = candidates[index];

            try
            {
                _mediaPlayer.Stop();
                _currentMedia?.Dispose();
                _currentMedia = new Media(_libVlc, new Uri(candidate.Url));
                _currentMedia.AddOption(":network-caching=1200");
                _currentMedia.AddOption(":live-caching=1200");
                _currentMedia.AddOption(":http-reconnect=true");
                _currentMedia.AddOption(":http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/152 Safari/537.36");

                if (!string.IsNullOrWhiteSpace(candidate.Referrer))
                {
                    _currentMedia.AddOption($":http-referrer={candidate.Referrer}");
                }

                _statusLabel.Text = index == 0
                    ? "Σύνδεση με το κανάλι..."
                    : $"Εναλλακτική σύνδεση {index + 1}/{candidates.Count}...";

                if (!_mediaPlayer.Play(_currentMedia))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            var deadline = DateTime.UtcNow.AddSeconds(9);
            while (DateTime.UtcNow < deadline)
            {
                if (generation != _playbackGeneration || _currentChannel != channel)
                {
                    return;
                }

                var state = _mediaPlayer.State;
                if (state is VLCState.Playing or VLCState.Paused)
                {
                    return;
                }

                if (state is VLCState.Error or VLCState.Ended)
                {
                    break;
                }

                await Task.Delay(300);
            }
        }

        if (generation == _playbackGeneration && _currentChannel == channel)
        {
            _playPauseButton.Text = "▶";
            _statusLabel.Text = channel.Name is "ERT 1" or "ERT 2" or "ERT 3"
                ? "Δεν βρέθηκε διαθέσιμο απευθείας stream της ΕΡΤ αυτή τη στιγμή."
                : "Το κανάλι δεν είναι διαθέσιμο. Πάτησε ↻ για νέα προσπάθεια.";
        }
    }

    private static List<(string Url, string Referrer)> GetPlaybackCandidates(Channel channel)
    {
        const string ert = "https://www.ert.gr/tv-live/";

        return channel.Name switch
        {
            "Star" => new List<(string, string)>
            {
                ("https://livestar.siliconweb.com/starvod/star4/star4mediumhd.m3u8", "https://www.star.gr/tv/live-stream"),
                ("https://livestar.siliconweb.com/starvod/star4/star4.m3u8", "https://www.star.gr/tv/live-stream"),
                ("https://livestar.siliconweb.com/media/star4/star4.m3u8", "https://www.star.gr/tv/live-stream")
            },
            "AlphaTV" => new List<(string, string)>
            {
                ("https://alphatvlive2.siliconweb.com/alphatvlive/live_abr/alphatvlive/live_720p/chunks.m3u8", "https://www.alphatv.gr/live"),
                ("https://alphatvlive2.siliconweb.com/alphatvlive/live_abr/playlist.m3u8", "https://www.alphatv.gr/live"),
                ("https://alphatvlive.siliconweb.com/1/Y2Rsd1lUcUVoajcv/UVdCN25h/hls/live/playlist.m3u8", "https://www.alphatv.gr/live")
            },
            "Skai TV" => new List<(string, string)>
            {
                ("https://skai-live.siliconweb.com/media/cambria4/index_media_bitrate1200K_avc1_mp4a.m3u8", "https://www.skai.gr/tv/live"),
                ("https://skai-live.siliconweb.com/media/cambria4/index.m3u8", "https://www.skai.gr/tv/live"),
                ("https://skai-live-back.siliconweb.com/media/cambria4/index_bitrate2000K.m3u8", "https://www.skai.gr/tv/live")
            },
            "Open TV" => new List<(string, string)>
            {
                ("https://liveopen.siliconweb.com/openTvLive/liveopen/chunks.m3u8", "https://www.tvopen.gr/live"),
                ("https://liveopen.siliconweb.com/openTvLive/liveopen/playlist.m3u8", "https://www.tvopen.gr/live"),
                ("https://liveopencloud.siliconweb.com/1/ZlRza2R6L2tFRnFJ/eWVLSlQx/hls/live/playlist.m3u8", "https://www.tvopen.gr/live")
            },
            "ERT 1" => new List<(string, string)>
            {
                ("https://ertflix.s.llnwi.net/ertlive/ert1/clrdef24723b/playlist.m3u8", ert),
                ("https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERT1/default/index.m3u8", ert),
                ("https://eu5cdn.overotm.com/abr_amd10/ert1/playlist.m3u8", ert)
            },
            "ERT 2" => new List<(string, string)>
            {
                ("https://ertflix.s.llnwi.net/ertlive/ert2/clrdef24828z/playlist.m3u8", ert),
                ("https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERT2/default/index.m3u8", ert)
            },
            "ERT 3" => new List<(string, string)>
            {
                ("https://ertflix.s.llnwi.net/ertlive/ert3/clrdef24828n/playlist.m3u8", ert),
                ("https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERT3/default/index.m3u8", ert)
            },
            _ => new List<(string, string)> { (channel.Url, string.Empty) }
        };
    }

'@

$text = $text.Substring(0, $start) + $replacement + $text.Substring($end)
Set-Content -LiteralPath $path -Value $text -Encoding utf8
