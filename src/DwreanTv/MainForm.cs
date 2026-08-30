using System.Diagnostics;
using DwreanTv.Models;
using DwreanTv.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace DwreanTv;

public sealed class MainForm : Form
{
    private const string AppVersion = "0.2.0";
    private const string DwreanUrl = "https://www.dwrean.net/";
    private const string SourcePageUrl = "https://github.com/Free-TV/IPTV/blob/master/lists/greece.md";

    private readonly Color _background = Color.FromArgb(17, 19, 24);
    private readonly Color _panel = Color.FromArgb(25, 28, 35);
    private readonly Color _panelHover = Color.FromArgb(35, 39, 48);
    private readonly Color _accent = Color.FromArgb(235, 45, 50);
    private readonly Color _text = Color.FromArgb(242, 243, 245);
    private readonly Color _muted = Color.FromArgb(157, 163, 175);

    private readonly ChannelService _channelService = new();
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private Media? _currentMedia;

    private readonly Panel _headerPanel;
    private readonly Panel _sidebarPanel;
    private readonly Panel _controlsPanel;
    private readonly VideoView _videoView;
    private readonly FlowLayoutPanel _channelFlow;
    private readonly TextBox _searchBox;
    private readonly ComboBox _categoryCombo;
    private readonly Button _favoritesFilterButton;
    private readonly Button _refreshButton;
    private readonly Button _playPauseButton;
    private readonly Button _muteButton;
    private readonly Button _favoriteCurrentButton;
    private readonly Button _retryButton;
    private readonly Button _fullScreenButton;
    private readonly TrackBar _volumeTrackBar;
    private readonly Label _nowPlayingLabel;
    private readonly Label _statusLabel;

    private List<Channel> _allChannels = new();
    private Channel? _currentChannel;
    private bool _showFavoritesOnly;
    private bool _isFullScreen;
    private FormBorderStyle _previousBorderStyle;
    private FormWindowState _previousWindowState;

    public MainForm()
    {
        Text = "dwrean Ελληνική Τηλεόραση";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 700);
        Size = new Size(1360, 820);
        BackColor = _background;
        ForeColor = _text;
        Font = new Font("Segoe UI", 9F);
        KeyPreview = true;

        try
        {
            var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (appIcon is not null)
            {
                Icon = appIcon;
            }
        }
        catch
        {
            // The application remains usable even if Windows cannot read the executable icon.
        }

        _settings = _settingsService.Load();

        _libVlc = new LibVLC("--no-video-title-show", "--quiet");
        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            Volume = Math.Clamp(_settings.Volume, 0, 100)
        };

        _headerPanel = BuildHeader();
        _sidebarPanel = BuildSidebar(out _searchBox, out _categoryCombo, out _favoritesFilterButton, out _refreshButton, out _channelFlow);
        _controlsPanel = BuildControls(
            out _playPauseButton,
            out _muteButton,
            out _favoriteCurrentButton,
            out _retryButton,
            out _fullScreenButton,
            out _volumeTrackBar,
            out _nowPlayingLabel,
            out _statusLabel);

        _videoView = new VideoView
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            MediaPlayer = _mediaPlayer
        };

        var videoHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            Padding = new Padding(0)
        };
        videoHost.Controls.Add(_videoView);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _background
        };
        body.Controls.Add(videoHost);
        body.Controls.Add(_sidebarPanel);

        Controls.Add(body);
        Controls.Add(_controlsPanel);
        Controls.Add(_headerPanel);

        WireEvents();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadChannelsAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _settings.Volume = _mediaPlayer.Volume;
        _settingsService.Save(_settings);

        _mediaPlayer.Stop();
        _currentMedia?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();

        base.OnFormClosed(e);
    }

    private Panel BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 82,
            BackColor = Color.FromArgb(20, 22, 28),
            Padding = new Padding(20, 0, 20, 0)
        };

        var appLogo = new PictureBox
        {
            Width = 48,
            Height = 48,
            Location = new Point(20, 17),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        };

        try
        {
            appLogo.Image = Icon?.ToBitmap();
        }
        catch
        {
            // Keep an empty logo box if the icon cannot be converted to a bitmap.
        }

        var dwreanLabel = new Label
        {
            Text = "dwrean",
            ForeColor = _accent,
            Font = new Font("Segoe UI Black", 17F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(79, 13)
        };

        var title = new Label
        {
            Text = "Ελληνική Τηλεόραση",
            ForeColor = _text,
            Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(161, 13)
        };

        var subtitle = new Label
        {
            Text = "Δωρεάν ελληνικά τηλεοπτικά κανάλια • χωρίς εγκατάσταση",
            ForeColor = _muted,
            Font = new Font("Segoe UI", 8.8F),
            AutoSize = true,
            Location = new Point(81, 48)
        };

        var siteLink = new LinkLabel
        {
            Text = "dwrean.net",
            AutoSize = true,
            LinkColor = Color.FromArgb(255, 103, 103),
            ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.FromArgb(255, 103, 103),
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        siteLink.LinkClicked += (_, _) => OpenUrl(DwreanUrl);

        var aboutButton = new Button
        {
            Text = "ⓘ  Σχετικά",
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 1, BorderColor = Color.FromArgb(60, 65, 76) },
            BackColor = Color.FromArgb(31, 35, 43),
            ForeColor = _text,
            Cursor = Cursors.Hand,
            Width = 102,
            Height = 34,
            Font = new Font("Segoe UI Semibold", 9F)
        };
        aboutButton.Click += (_, _) => ShowAboutDialog();

        void PositionRightControls()
        {
            aboutButton.Left = panel.ClientSize.Width - aboutButton.Width - 20;
            aboutButton.Top = 24;
            siteLink.Left = aboutButton.Left - siteLink.Width - 22;
            siteLink.Top = 32;
        }

        panel.Resize += (_, _) => PositionRightControls();
        PositionRightControls();

        panel.Controls.Add(appLogo);
        panel.Controls.Add(dwreanLabel);
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        panel.Controls.Add(siteLink);
        panel.Controls.Add(aboutButton);
        return panel;
    }

    private Panel BuildSidebar(
        out TextBox searchBox,
        out ComboBox categoryCombo,
        out Button favoritesButton,
        out Button refreshButton,
        out FlowLayoutPanel channelFlow)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Left,
            Width = 350,
            BackColor = _panel,
            Padding = new Padding(16)
        };

        var filters = new Panel
        {
            Dock = DockStyle.Top,
            Height = 142,
            BackColor = _panel
        };

        searchBox = new TextBox
        {
            PlaceholderText = "Αναζήτηση καναλιού...",
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(34, 38, 46),
            ForeColor = _text,
            Font = new Font("Segoe UI", 10F),
            Location = new Point(0, 0),
            Width = 318,
            Height = 34
        };

        categoryCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(34, 38, 46),
            ForeColor = _text,
            Font = new Font("Segoe UI", 9F),
            Location = new Point(0, 46),
            Width = 318
        };
        categoryCombo.Items.Add("Όλα τα κανάλια");
        categoryCombo.SelectedIndex = 0;

        favoritesButton = CreateSmallButton("☆  Αγαπημένα", 0, 88, 154);
        refreshButton = CreateSmallButton("↻  Ανανέωση", 164, 88, 154);

        filters.Controls.Add(searchBox);
        filters.Controls.Add(categoryCombo);
        filters.Controls.Add(favoritesButton);
        filters.Controls.Add(refreshButton);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 66,
            BackColor = Color.FromArgb(21, 24, 30),
            Padding = new Padding(0, 9, 0, 0)
        };

        var creator = new Label
        {
            Text = "Δημιουργία: Κυριάκος Οικονομίδης",
            ForeColor = _muted,
            Font = new Font("Segoe UI", 8F),
            AutoSize = true,
            Location = new Point(8, 10)
        };

        var footerLink = new LinkLabel
        {
            Text = $"dwrean.net  •  v{AppVersion}",
            LinkColor = Color.FromArgb(255, 103, 103),
            ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.FromArgb(255, 103, 103),
            Font = new Font("Segoe UI Semibold", 8.2F),
            AutoSize = true,
            Cursor = Cursors.Hand,
            Location = new Point(8, 35)
        };
        footerLink.LinkClicked += (_, _) => OpenUrl(DwreanUrl);

        footer.Controls.Add(creator);
        footer.Controls.Add(footerLink);

        channelFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = _panel,
            Padding = new Padding(0, 4, 0, 0)
        };

        panel.Controls.Add(channelFlow);
        panel.Controls.Add(footer);
        panel.Controls.Add(filters);
        return panel;
    }

    private Panel BuildControls(
        out Button playPauseButton,
        out Button muteButton,
        out Button favoriteButton,
        out Button retryButton,
        out Button fullScreenButton,
        out TrackBar volumeTrackBar,
        out Label nowPlaying,
        out Label status)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 84,
            BackColor = Color.FromArgb(20, 22, 28),
            Padding = new Padding(18, 10, 18, 10)
        };

        playPauseButton = CreateControlButton("▶", 18, 20, 48);
        muteButton = CreateControlButton("🔊", 76, 20, 48);

        volumeTrackBar = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(_settings.Volume, 0, 100),
            TickStyle = TickStyle.None,
            Width = 130,
            Height = 35,
            Location = new Point(130, 25)
        };

        favoriteButton = CreateControlButton("☆", 274, 20, 48);
        retryButton = CreateControlButton("↻", 332, 20, 48);
        retryButton.AccessibleName = "Επαναφόρτωση καναλιού";
        fullScreenButton = CreateControlButton("⛶", 390, 20, 48);

        nowPlaying = new Label
        {
            Text = "Επίλεξε ένα κανάλι",
            ForeColor = _text,
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(463, 18)
        };

        status = new Label
        {
            Text = "Φόρτωση λίστας...",
            ForeColor = _muted,
            Font = new Font("Segoe UI", 8.5F),
            AutoSize = true,
            Location = new Point(463, 43)
        };

        panel.Controls.Add(playPauseButton);
        panel.Controls.Add(muteButton);
        panel.Controls.Add(volumeTrackBar);
        panel.Controls.Add(favoriteButton);
        panel.Controls.Add(retryButton);
        panel.Controls.Add(fullScreenButton);
        panel.Controls.Add(nowPlaying);
        panel.Controls.Add(status);
        return panel;
    }

    private Button CreateSmallButton(string text, int x, int y, int width)
    {
        return new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = Color.FromArgb(43, 47, 57),
            ForeColor = _text,
            Cursor = Cursors.Hand,
            Location = new Point(x, y),
            Width = width,
            Height = 34,
            Font = new Font("Segoe UI Semibold", 9F)
        };
    }

    private Button CreateControlButton(string text, int x, int y, int size)
    {
        return new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = Color.FromArgb(43, 47, 57),
            ForeColor = _text,
            Cursor = Cursors.Hand,
            Location = new Point(x, y),
            Width = size,
            Height = 42,
            Font = new Font("Segoe UI Symbol", 12F, FontStyle.Bold)
        };
    }

    private void WireEvents()
    {
        _searchBox.TextChanged += (_, _) => RefreshChannelCards();
        _categoryCombo.SelectedIndexChanged += (_, _) => RefreshChannelCards();

        _favoritesFilterButton.Click += (_, _) =>
        {
            _showFavoritesOnly = !_showFavoritesOnly;
            _favoritesFilterButton.Text = _showFavoritesOnly ? "★  Αγαπημένα" : "☆  Αγαπημένα";
            _favoritesFilterButton.BackColor = _showFavoritesOnly ? _accent : Color.FromArgb(43, 47, 57);
            RefreshChannelCards();
        };

        _refreshButton.Click += async (_, _) => await LoadChannelsAsync(true);

        _playPauseButton.Click += (_, _) =>
        {
            if (_currentChannel is null) return;

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
            }
            else if (_mediaPlayer.Media is not null)
            {
                _mediaPlayer.Play();
            }
            else
            {
                PlayChannel(_currentChannel);
            }
        };

        _muteButton.Click += (_, _) =>
        {
            _mediaPlayer.Mute = !_mediaPlayer.Mute;
            _muteButton.Text = _mediaPlayer.Mute ? "🔇" : "🔊";
        };

        _volumeTrackBar.ValueChanged += (_, _) =>
        {
            _mediaPlayer.Volume = _volumeTrackBar.Value;
            _settings.Volume = _volumeTrackBar.Value;
        };

        _favoriteCurrentButton.Click += (_, _) =>
        {
            if (_currentChannel is not null)
            {
                ToggleFavorite(_currentChannel);
            }
        };

        _retryButton.Click += (_, _) =>
        {
            if (_currentChannel is not null)
            {
                PlayChannel(_currentChannel);
            }
        };

        _fullScreenButton.Click += (_, _) => ToggleFullScreen();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _isFullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        };

        _mediaPlayer.Playing += (_, _) => SafeUi(() =>
        {
            _playPauseButton.Text = "Ⅱ";
            _statusLabel.Text = "Αναπαραγωγή σε εξέλιξη";
        });

        _mediaPlayer.Paused += (_, _) => SafeUi(() =>
        {
            _playPauseButton.Text = "▶";
            _statusLabel.Text = "Παύση";
        });

        _mediaPlayer.Stopped += (_, _) => SafeUi(() => _playPauseButton.Text = "▶");

        _mediaPlayer.EncounteredError += (_, _) => SafeUi(() =>
        {
            _playPauseButton.Text = "▶";
            _statusLabel.Text = "Το κανάλι δεν είναι διαθέσιμο. Πάτησε ↻ για νέα προσπάθεια.";
        });
    }

    private async Task LoadChannelsAsync(bool forceRefresh = false)
    {
        _refreshButton.Enabled = false;
        _refreshButton.Text = "↻  Φόρτωση...";
        _statusLabel.Text = "Ενημέρωση λίστας τηλεοπτικών καναλιών...";

        try
        {
            var result = await _channelService.LoadAsync(forceRefresh);
            _allChannels = result.Channels.ToList();
            PopulateCategories();
            RefreshChannelCards();

            var source = result.FromWeb ? "online" : "από το τελευταίο αποθηκευμένο αντίγραφο";
            var updated = result.UpdatedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—";
            _statusLabel.Text = $"{_allChannels.Count} τηλεοπτικά κανάλια • λίστα {source} • ενημέρωση {updated}";

            if (_currentChannel is null && !string.IsNullOrWhiteSpace(_settings.LastChannelKey))
            {
                var last = _allChannels.FirstOrDefault(c =>
                    string.Equals(SettingsService.GetChannelKey(c), _settings.LastChannelKey, StringComparison.OrdinalIgnoreCase));

                if (last is not null)
                {
                    PlayChannel(last);
                }
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Δεν ήταν δυνατή η φόρτωση της λίστας καναλιών.";
            MessageBox.Show(
                $"Δεν ήταν δυνατή η φόρτωση των τηλεοπτικών καναλιών.\n\n{ex.Message}",
                "dwrean Ελληνική Τηλεόραση",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _refreshButton.Enabled = true;
            _refreshButton.Text = "↻  Ανανέωση";
        }
    }

    private void PopulateCategories()
    {
        var selected = _categoryCombo.SelectedItem?.ToString() ?? "Όλα τα κανάλια";
        _categoryCombo.BeginUpdate();
        _categoryCombo.Items.Clear();
        _categoryCombo.Items.Add("Όλα τα κανάλια");

        foreach (var category in _allChannels.Select(c => c.Category).Distinct().OrderBy(x => x))
        {
            _categoryCombo.Items.Add(category);
        }

        var index = _categoryCombo.Items.IndexOf(selected);
        _categoryCombo.SelectedIndex = index >= 0 ? index : 0;
        _categoryCombo.EndUpdate();
    }

    private void RefreshChannelCards()
    {
        var search = _searchBox.Text.Trim();
        var category = _categoryCombo.SelectedItem?.ToString() ?? "Όλα τα κανάλια";

        var filtered = _allChannels.Where(channel =>
        {
            var matchesSearch = string.IsNullOrWhiteSpace(search) ||
                                channel.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase);
            var matchesCategory = category == "Όλα τα κανάλια" || channel.Category == category;
            var matchesFavorites = !_showFavoritesOnly || IsFavorite(channel);
            return matchesSearch && matchesCategory && matchesFavorites;
        }).ToList();

        _channelFlow.SuspendLayout();
        _channelFlow.Controls.Clear();

        foreach (var channel in filtered)
        {
            _channelFlow.Controls.Add(CreateChannelCard(channel));
        }

        if (filtered.Count == 0)
        {
            _channelFlow.Controls.Add(new Label
            {
                Text = _showFavoritesOnly ? "Δεν έχεις προσθέσει αγαπημένα κανάλια." : "Δεν βρέθηκαν κανάλια.",
                ForeColor = _muted,
                AutoSize = false,
                Width = 300,
                Height = 70,
                TextAlign = ContentAlignment.MiddleCenter
            });
        }

        _channelFlow.ResumeLayout();
    }

    private Control CreateChannelCard(Channel channel)
    {
        var card = new Panel
        {
            Width = 305,
            Height = 64,
            Margin = new Padding(0, 0, 0, 7),
            BackColor = _currentChannel == channel ? Color.FromArgb(52, 38, 40) : Color.FromArgb(31, 35, 43),
            Cursor = Cursors.Hand,
            Tag = channel
        };

        var logo = new PictureBox
        {
            Width = 44,
            Height = 44,
            Location = new Point(10, 10),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            Tag = channel
        };

        if (!string.IsNullOrWhiteSpace(channel.LogoUrl))
        {
            try
            {
                logo.ImageLocation = channel.LogoUrl;
            }
            catch
            {
                // A missing logo must not affect playback.
            }
        }

        var name = new Label
        {
            Text = channel.Name,
            ForeColor = _text,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            AutoEllipsis = true,
            AutoSize = false,
            Width = 178,
            Height = 24,
            Location = new Point(64, 10),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
            Tag = channel
        };

        var metaText = channel.GeoBlocked ? $"{channel.Category} • Geo" : channel.Category;
        var meta = new Label
        {
            Text = metaText,
            ForeColor = _muted,
            Font = new Font("Segoe UI", 7.8F),
            AutoEllipsis = true,
            AutoSize = false,
            Width = 178,
            Height = 20,
            Location = new Point(64, 34),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
            Tag = channel
        };

        var star = new Button
        {
            Text = IsFavorite(channel) ? "★" : "☆",
            ForeColor = IsFavorite(channel) ? Color.Gold : _muted,
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = _panelHover },
            Font = new Font("Segoe UI Symbol", 13F),
            Width = 45,
            Height = 44,
            Location = new Point(252, 10),
            Cursor = Cursors.Hand,
            Tag = channel
        };

        void playHandler(object? _, EventArgs __) => PlayChannel(channel);
        card.Click += playHandler;
        logo.Click += playHandler;
        name.Click += playHandler;
        meta.Click += playHandler;

        star.Click += (_, _) => ToggleFavorite(channel);

        card.MouseEnter += (_, _) =>
        {
            if (_currentChannel != channel) card.BackColor = _panelHover;
        };
        card.MouseLeave += (_, _) =>
        {
            if (_currentChannel != channel) card.BackColor = Color.FromArgb(31, 35, 43);
        };

        card.Controls.Add(logo);
        card.Controls.Add(name);
        card.Controls.Add(meta);
        card.Controls.Add(star);
        return card;
    }

    private void PlayChannel(Channel channel)
    {
        _currentChannel = channel;
        _settings.LastChannelKey = SettingsService.GetChannelKey(channel);
        _settingsService.Save(_settings);
        UpdateCurrentFavoriteButton();
        RefreshChannelCards();

        _nowPlayingLabel.Text = channel.Name;

        try
        {
            _mediaPlayer.Stop();
            _currentMedia?.Dispose();
            _currentMedia = new Media(_libVlc, new Uri(channel.Url));
            _currentMedia.AddOption(":network-caching=1800");
            _currentMedia.AddOption(":live-caching=1800");
            _mediaPlayer.Play(_currentMedia);
            _statusLabel.Text = "Σύνδεση με το κανάλι...";
        }
        catch
        {
            _playPauseButton.Text = "▶";
            _statusLabel.Text = "Το κανάλι δεν είναι διαθέσιμο. Πάτησε ↻ για νέα προσπάθεια.";
        }
    }

    private bool IsFavorite(Channel channel)
    {
        return _settings.FavoriteKeys.Contains(SettingsService.GetChannelKey(channel));
    }

    private void ToggleFavorite(Channel channel)
    {
        var key = SettingsService.GetChannelKey(channel);
        if (!_settings.FavoriteKeys.Add(key))
        {
            _settings.FavoriteKeys.Remove(key);
        }

        _settingsService.Save(_settings);
        UpdateCurrentFavoriteButton();
        RefreshChannelCards();
    }

    private void UpdateCurrentFavoriteButton()
    {
        var favorite = _currentChannel is not null && IsFavorite(_currentChannel);
        _favoriteCurrentButton.Text = favorite ? "★" : "☆";
        _favoriteCurrentButton.ForeColor = favorite ? Color.Gold : _text;
    }

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _previousBorderStyle = FormBorderStyle;
            _previousWindowState = WindowState;
            _headerPanel.Visible = false;
            _sidebarPanel.Visible = false;
            _controlsPanel.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            _isFullScreen = true;
        }
        else
        {
            TopMost = false;
            FormBorderStyle = _previousBorderStyle;
            WindowState = _previousWindowState;
            _headerPanel.Visible = true;
            _sidebarPanel.Visible = true;
            _controlsPanel.Visible = true;
            _isFullScreen = false;
        }
    }

    private void ShowAboutDialog()
    {
        using var dialog = new Form
        {
            Text = "Σχετικά με το dwrean Ελληνική Τηλεόραση",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(510, 385),
            BackColor = _background,
            ForeColor = _text,
            Font = new Font("Segoe UI", 9F)
        };

        if (Icon is not null)
        {
            dialog.Icon = Icon;
        }

        var logo = new PictureBox
        {
            Width = 58,
            Height = 58,
            Location = new Point(24, 22),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = Icon?.ToBitmap()
        };

        var title = new Label
        {
            Text = "dwrean Ελληνική Τηλεόραση",
            ForeColor = _text,
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(98, 23)
        };

        var version = new Label
        {
            Text = $"Έκδοση {AppVersion} • Portable για Windows 64-bit",
            ForeColor = _muted,
            AutoSize = true,
            Location = new Point(100, 57)
        };

        var creator = new Label
        {
            Text = "Δημιουργία: Κυριάκος Οικονομίδης",
            ForeColor = _text,
            Font = new Font("Segoe UI Semibold", 9.5F),
            AutoSize = true,
            Location = new Point(26, 112)
        };

        var siteCaption = new Label
        {
            Text = "Ιστότοπος:",
            ForeColor = _muted,
            AutoSize = true,
            Location = new Point(26, 147)
        };

        var site = CreateDialogLink("dwrean.net", 99, 147, DwreanUrl);

        var sourceCaption = new Label
        {
            Text = "Πηγή λίστας:",
            ForeColor = _muted,
            AutoSize = true,
            Location = new Point(26, 180)
        };

        var source = CreateDialogLink("Free-TV / IPTV – Greece", 117, 180, SourcePageUrl);

        var info = new Label
        {
            Text = "Η εφαρμογή εμφανίζει δωρεάν διαθέσιμα ελληνικά τηλεοπτικά streams από δημόσια online λίστα. Δεν φιλοξενεί ούτε αναμεταδίδει η ίδια τηλεοπτικό περιεχόμενο. Τα κανάλια YouTube και η ενότητα ραδιοφώνου δεν περιλαμβάνονται.",
            ForeColor = _muted,
            AutoSize = false,
            Location = new Point(26, 222),
            Size = new Size(458, 82)
        };

        var closeButton = new Button
        {
            Text = "Κλείσιμο",
            Width = 105,
            Height = 34,
            Location = new Point(379, 327),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = _accent,
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        closeButton.Click += (_, _) => dialog.Close();

        dialog.Controls.Add(logo);
        dialog.Controls.Add(title);
        dialog.Controls.Add(version);
        dialog.Controls.Add(creator);
        dialog.Controls.Add(siteCaption);
        dialog.Controls.Add(site);
        dialog.Controls.Add(sourceCaption);
        dialog.Controls.Add(source);
        dialog.Controls.Add(info);
        dialog.Controls.Add(closeButton);
        dialog.ShowDialog(this);
    }

    private LinkLabel CreateDialogLink(string text, int x, int y, string url)
    {
        var link = new LinkLabel
        {
            Text = text,
            LinkColor = Color.FromArgb(255, 103, 103),
            ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.FromArgb(255, 103, 103),
            AutoSize = true,
            Cursor = Cursors.Hand,
            Location = new Point(x, y)
        };
        link.LinkClicked += (_, _) => OpenUrl(url);
        return link;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // External links are optional and must never interrupt TV playback.
        }
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
