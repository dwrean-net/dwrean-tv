using System.Drawing.Drawing2D;
using System.Reflection;
using DwreanTv.Models;
using LibVLCSharp.Shared;

namespace DwreanTv;

internal static class Program
{
    private const string CurrentVersion = "0.2.2";

    [STAThread]
    private static void Main()
    {
        Core.Initialize();
        ApplicationConfiguration.Initialize();

        var mainForm = new MainForm();
        _ = new PlaybackCoordinator(mainForm);
        ApplyFinalPolish(mainForm);

        Application.Idle += (_, _) =>
        {
            foreach (Form openForm in Application.OpenForms)
            {
                RefreshDisplayedVersion(openForm);
            }
        };

        Application.Run(mainForm);
    }

    private sealed class PlaybackCoordinator
    {
        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

        private static readonly Dictionary<string, string[]> Fallbacks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ERT1.gr"] =
            [
                "https://ert-live.siliconweb.com/bpk-tv/ERT1/default/index.mpd",
                "https://ertflix.s.llnwi.net/ertlive/ert1/clrdef24723b/playlist.m3u8",
                "https://eu5cdn.overotm.com/abr_amd10/ert1/playlist.m3u8"
            ],
            ["ERT2.gr"] =
            [
                "https://ert-live.siliconweb.com/bpk-tv/ERT2/default/index.mpd",
                "https://ertflix.s.llnwi.net/ertlive/ert2/clrdef24828z/playlist.m3u8"
            ],
            ["ERT3.gr"] =
            [
                "https://ert-live.siliconweb.com/bpk-tv/ERT3/default/index.mpd",
                "https://ertflix.s.llnwi.net/ertlive/ert3/clrdef24828n/playlist.m3u8"
            ],
            ["ERTNews.gr"] =
            [
                "https://ert-ucdn.broadpeak-aas.com/bpk-tv/ERTNews/default/index.mpd",
                "https://ertflix.s.llnwi.net/ertlive/ertnews/default/playlist.m3u8",
                "http://hbbtvapp.ert.gr/stream.php/v/vid_ertnews_mpeg.2ts"
            ],
            ["ANT1.gr"] =
            [
                "https://mcdn.antennaplus.gr/live/media0/Ant1/HLS/Ant1.m3u8",
                "https://cdn1.smart-tv-data.com/live/ant1/playlist.m3u8",
                "https://lcdn.antennaplus.gr/r86d08d448885424196f6cd3ddc5d1489/eu-central-1/6415884360001/playlist_dvr.m3u8"
            ],
            ["StarChannel.gr"] =
            [
                "https://livestar.siliconweb.com/starvod/star4/star4.m3u8",
                "https://livestar.siliconweb.com/media/star4/star4.m3u8"
            ],
            ["AlphaTV.gr"] =
            [
                "https://alphatvlive2.siliconweb.com/alphatvlive/live_abr/playlist.m3u8",
                "https://alphatvlive.siliconweb.com/1/Y2Rsd1lUcUVoajcv/UVdCN25h/hls/live/playlist.m3u8",
                "http://alphatvlive.siliconweb.com/1/Y2Rsd1lUcUVoajcv/UVdCN25h/hls/live/playlist.m3u8"
            ],
            ["SkaiTV.gr"] =
            [
                "http://skai-live.siliconweb.com/media/cambria4/index.m3u8",
                "https://skai-live.siliconweb.com/media/cambria4/index.m3u8",
                "https://skai-live-back.siliconweb.com/media/cambria4/index_bitrate2000K.m3u8"
            ],
            ["OpenTV.gr"] =
            [
                "https://liveopen.siliconweb.com/openTvLive/liveopen/playlist.m3u8",
                "https://liveopencloud.siliconweb.com/1/ZlRza2R6L2tFRnFJ/eWVLSlQx/hls/live/playlist.m3u8"
            ],
            ["MakTV.gr"] =
            [
                "https://mcdn.antennaplus.gr/live/media0/MAK/HLS/MAK.m3u8"
            ]
        };

        private readonly MainForm _form;
        private readonly MediaPlayer _player;
        private readonly LibVLC _libVlc;
        private readonly FieldInfo _currentMediaField;
        private readonly FieldInfo _currentChannelField;
        private readonly Label? _statusLabel;
        private readonly Button? _playPauseButton;
        private readonly System.Windows.Forms.Timer _startupTimer;

        private string _activeChannelKey = string.Empty;
        private IReadOnlyList<string> _activeCandidates = Array.Empty<string>();
        private int _candidateIndex = -1;
        private bool _internalMediaChange;
        private bool _switchInProgress;

        public PlaybackCoordinator(MainForm form)
        {
            _form = form;

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _player = (MediaPlayer)(typeof(MainForm).GetField("_mediaPlayer", flags)?.GetValue(form)
                ?? throw new InvalidOperationException("Media player not found."));
            _libVlc = (LibVLC)(typeof(MainForm).GetField("_libVlc", flags)?.GetValue(form)
                ?? throw new InvalidOperationException("LibVLC not found."));
            _currentMediaField = typeof(MainForm).GetField("_currentMedia", flags)
                ?? throw new InvalidOperationException("Current media field not found.");
            _currentChannelField = typeof(MainForm).GetField("_currentChannel", flags)
                ?? throw new InvalidOperationException("Current channel field not found.");
            _statusLabel = typeof(MainForm).GetField("_statusLabel", flags)?.GetValue(form) as Label;
            _playPauseButton = typeof(MainForm).GetField("_playPauseButton", flags)?.GetValue(form) as Button;

            _startupTimer = new System.Windows.Forms.Timer { Interval = 7000 };
            _startupTimer.Tick += (_, _) =>
            {
                _startupTimer.Stop();
                if (!_player.IsPlaying)
                {
                    TryNextCandidate();
                }
            };

            _player.MediaChanged += OnMediaChanged;
            _player.Playing += (_, _) => OnPlaying();
            _player.EncounteredError += (_, _) => SafeUi(TryNextCandidate);
        }

        private void OnMediaChanged(object? sender, MediaPlayerMediaChangedEventArgs e)
        {
            if (_internalMediaChange)
            {
                _internalMediaChange = false;
                RestartStartupTimer();
                return;
            }

            var channel = _currentChannelField.GetValue(_form) as Channel;
            if (channel is null || string.IsNullOrWhiteSpace(channel.EpgId) || !Fallbacks.TryGetValue(channel.EpgId, out var knownCandidates))
            {
                return;
            }

            _activeChannelKey = ChannelKey(channel);
            _activeCandidates = BuildCandidateList(channel, knownCandidates);
            _candidateIndex = -1;

            SafeUi(() => StartCandidate(0));
        }

        private static IReadOnlyList<string> BuildCandidateList(Channel channel, IEnumerable<string> knownCandidates)
        {
            var result = new List<string>();

            foreach (var url in knownCandidates)
            {
                AddUnique(result, url);
            }

            // Keep the URL currently supplied by Free-TV as a final safety net,
            // unless it is one of the incorrect temporary ERT compatibility URLs
            // used by v0.2.1.
            if (!channel.Url.Contains("ertflix.s.llnwi.net/ertlive/ert", StringComparison.OrdinalIgnoreCase) ||
                channel.Url.Contains("clrdef", StringComparison.OrdinalIgnoreCase) ||
                channel.Url.Contains("/playlist.m3u8", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(result, channel.Url);
            }

            return result;
        }

        private static void AddUnique(List<string> items, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !items.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(value);
            }
        }

        private void StartCandidate(int index)
        {
            if (_switchInProgress || index < 0 || index >= _activeCandidates.Count)
            {
                if (index >= _activeCandidates.Count && _activeCandidates.Count > 0)
                {
                    _startupTimer.Stop();
                    if (_statusLabel is not null)
                    {
                        _statusLabel.Text = "Δεν βρέθηκε διαθέσιμο stream για το κανάλι αυτή τη στιγμή.";
                    }
                    if (_playPauseButton is not null)
                    {
                        _playPauseButton.Text = "▶";
                    }
                }
                return;
            }

            var channel = _currentChannelField.GetValue(_form) as Channel;
            if (channel is null || ChannelKey(channel) != _activeChannelKey)
            {
                return;
            }

            _switchInProgress = true;
            try
            {
                _startupTimer.Stop();
                _candidateIndex = index;
                var url = _activeCandidates[index];

                _player.Stop();

                if (_currentMediaField.GetValue(_form) is Media oldMedia)
                {
                    oldMedia.Dispose();
                }

                var media = new Media(_libVlc, new Uri(url));
                ApplyOptions(media, url);
                _currentMediaField.SetValue(_form, media);

                if (_statusLabel is not null)
                {
                    _statusLabel.Text = _activeCandidates.Count > 1
                        ? $"Σύνδεση με το κανάλι... πηγή {index + 1}/{_activeCandidates.Count}"
                        : "Σύνδεση με το κανάλι...";
                }

                _internalMediaChange = true;
                _player.Play(media);
                RestartStartupTimer();
            }
            catch
            {
                SafeUi(TryNextCandidate);
            }
            finally
            {
                _switchInProgress = false;
            }
        }

        private void TryNextCandidate()
        {
            if (_switchInProgress || _activeCandidates.Count == 0)
            {
                return;
            }

            var channel = _currentChannelField.GetValue(_form) as Channel;
            if (channel is null || ChannelKey(channel) != _activeChannelKey)
            {
                return;
            }

            var next = _candidateIndex + 1;
            if (next < _activeCandidates.Count)
            {
                StartCandidate(next);
            }
            else
            {
                _startupTimer.Stop();
                if (_statusLabel is not null)
                {
                    _statusLabel.Text = "Το κανάλι δεν είναι διαθέσιμο από καμία εφεδρική πηγή αυτή τη στιγμή.";
                }
                if (_playPauseButton is not null)
                {
                    _playPauseButton.Text = "▶";
                }
            }
        }

        private void OnPlaying()
        {
            SafeUi(() =>
            {
                _startupTimer.Stop();
                if (_statusLabel is not null)
                {
                    _statusLabel.Text = _candidateIndex > 0
                        ? $"Αναπαραγωγή σε εξέλιξη • εφεδρική πηγή {_candidateIndex + 1}"
                        : "Αναπαραγωγή σε εξέλιξη";
                }
            });
        }

        private void RestartStartupTimer()
        {
            SafeUi(() =>
            {
                _startupTimer.Stop();
                _startupTimer.Start();
            });
        }

        private static void ApplyOptions(Media media, string url)
        {
            media.AddOption($":http-user-agent={BrowserUserAgent}");
            media.AddOption(":http-reconnect=true");
            media.AddOption(":network-caching=2200");
            media.AddOption(":live-caching=2200");

            if (url.Contains("antennaplus.gr", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.antenna.gr/");
            }
            else if (url.Contains("alphatvlive", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.alphatv.gr/");
            }
            else if (url.Contains("livestar.siliconweb.com", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.star.gr/");
            }
            else if (url.Contains("skai-live", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.skai.gr/");
            }
            else if (url.Contains("liveopen", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.tvopen.gr/");
            }
        }

        private void SafeUi(Action action)
        {
            if (_form.IsDisposed)
            {
                return;
            }

            if (_form.InvokeRequired)
            {
                try
                {
                    _form.BeginInvoke(action);
                }
                catch
                {
                }
            }
            else
            {
                action();
            }
        }

        private static string ChannelKey(Channel channel) =>
            string.IsNullOrWhiteSpace(channel.EpgId)
                ? $"{channel.Name}|{channel.Url}"
                : channel.EpgId;
    }

    private static void ApplyFinalPolish(Form form)
    {
        PolishHeader(form);
        PolishChannelList(form);
        RefreshDisplayedVersion(form);
    }

    private static void RefreshDisplayedVersion(Control root)
    {
        root.Text = root.Text
            .Replace("0.2.0", CurrentVersion, StringComparison.Ordinal)
            .Replace("0.2.1", CurrentVersion, StringComparison.Ordinal);

        foreach (Control child in root.Controls)
        {
            RefreshDisplayedVersion(child);
        }
    }

    private static void PolishHeader(Form form)
    {
        var header = form.Controls
            .OfType<Panel>()
            .FirstOrDefault(panel => panel.Dock == DockStyle.Top);

        if (header is null)
        {
            return;
        }

        header.Height = 90;

        var logo = header.Controls.OfType<PictureBox>().FirstOrDefault();
        if (logo is not null)
        {
            header.Controls.Remove(logo);

            var logoFrame = new Panel
            {
                Location = new Point(24, 17),
                Size = new Size(56, 56),
                BackColor = Color.FromArgb(239, 240, 243),
                Padding = new Padding(5)
            };
            SetRoundedRegion(logoFrame, 10);

            logo.Dock = DockStyle.Fill;
            logo.BackColor = Color.Transparent;
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logoFrame.Controls.Add(logo);
            header.Controls.Add(logoFrame);
            logoFrame.BringToFront();
        }

        var dwrean = header.Controls
            .OfType<Label>()
            .FirstOrDefault(label => label.Text == "dwrean");
        if (dwrean is not null)
        {
            dwrean.Location = new Point(94, 16);
        }

        var title = header.Controls
            .OfType<Label>()
            .FirstOrDefault(label => label.Text == "Ελληνική Τηλεόραση");
        if (title is not null)
        {
            var dwreanWidth = dwrean is null
                ? 82
                : TextRenderer.MeasureText(dwrean.Text, dwrean.Font).Width;
            title.Location = new Point((dwrean?.Left ?? 94) + dwreanWidth + 12, 16);
        }

        var subtitle = header.Controls
            .OfType<Label>()
            .FirstOrDefault(label => label.Text.StartsWith("Δωρεάν ελληνικά", StringComparison.Ordinal));
        if (subtitle is not null)
        {
            subtitle.Location = new Point(96, 52);
        }
    }

    private static void PolishChannelList(Form form)
    {
        var sidebar = form.Controls
            .OfType<Panel>()
            .SelectMany(panel => panel.Controls.OfType<Panel>())
            .FirstOrDefault(panel => panel.Dock == DockStyle.Left);

        var flow = sidebar?.Controls.OfType<FlowLayoutPanel>().FirstOrDefault()
                   ?? FindControl<FlowLayoutPanel>(form);

        if (flow is null)
        {
            return;
        }

        flow.HorizontalScroll.Enabled = false;
        flow.HorizontalScroll.Visible = false;
        flow.AutoScrollMargin = Size.Empty;

        void ResizeAll()
        {
            foreach (Control child in flow.Controls)
            {
                ResizeFlowChild(flow, child);
            }

            flow.HorizontalScroll.Enabled = false;
            flow.HorizontalScroll.Visible = false;
        }

        flow.ControlAdded += (_, e) =>
        {
            ResizeFlowChild(flow, e.Control);
            flow.HorizontalScroll.Enabled = false;
            flow.HorizontalScroll.Visible = false;
        };
        flow.Resize += (_, _) => ResizeAll();
        ResizeAll();
    }

    private static void ResizeFlowChild(FlowLayoutPanel flow, Control child)
    {
        var availableWidth = Math.Max(
            260,
            flow.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10);

        child.Width = availableWidth;

        if (child is not Panel card || card.Height < 50)
        {
            return;
        }

        var star = card.Controls
            .OfType<Button>()
            .FirstOrDefault(button => button.Text is "★" or "☆");

        if (star is null)
        {
            return;
        }

        star.Left = card.ClientSize.Width - star.Width - 8;

        foreach (var label in card.Controls.OfType<Label>().Where(label => label.Left >= 60))
        {
            label.Width = Math.Max(90, star.Left - label.Left - 6);
        }
    }

    private static T? FindControl<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                return match;
            }

            var nested = FindControl<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void SetRoundedRegion(Control control, int radius)
    {
        var diameter = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(control.Width - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(control.Width - diameter, control.Height - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, control.Height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        control.Region = new Region(path);
    }
}
