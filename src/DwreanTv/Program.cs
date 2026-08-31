using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DwreanTv.Models;
using LibVLCSharp.Shared;

namespace DwreanTv;

internal static class Program
{
    private const string CurrentVersion = "0.2.5-mpv-test";

    [STAThread]
    private static void Main()
    {
        // MainForm still references LibVLC, therefore its native runtime must be initialized.
        // Actual TV playback in this test is handled exclusively by one persistent mpv process.
        Core.Initialize();
        ApplicationConfiguration.Initialize();

        var mainForm = new MainForm();
        using var mpvCoordinator = new MpvProcessCoordinator(mainForm);
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

    private sealed class MpvProcessCoordinator : IDisposable
    {
        private readonly MainForm _form;
        private readonly FieldInfo _currentChannelField;
        private readonly Control _videoHost;
        private readonly Label? _statusLabel;
        private readonly Button? _playPauseButton;
        private readonly Button? _muteButton;
        private readonly Button? _retryButton;
        private readonly TrackBar? _volumeTrackBar;
        private readonly MediaPlayer? _legacyPlayer;
        private readonly Panel _legacySink;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly SemaphoreSlim _switchGate = new(1, 1);

        private Process? _process;
        private string? _pipeName;
        private string _activeKey = string.Empty;
        private bool _paused;
        private bool _muted;
        private int _volume;
        private int _switchGeneration;
        private bool _disposed;

        public MpvProcessCoordinator(MainForm form)
        {
            _form = form;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            _currentChannelField = typeof(MainForm).GetField("_currentChannel", flags)
                ?? throw new InvalidOperationException("Current channel field not found.");

            _videoHost = (Control)(typeof(MainForm).GetField("_videoView", flags)?.GetValue(form)
                ?? throw new InvalidOperationException("Video host not found."));

            _statusLabel = typeof(MainForm).GetField("_statusLabel", flags)?.GetValue(form) as Label;
            _playPauseButton = typeof(MainForm).GetField("_playPauseButton", flags)?.GetValue(form) as Button;
            _muteButton = typeof(MainForm).GetField("_muteButton", flags)?.GetValue(form) as Button;
            _retryButton = typeof(MainForm).GetField("_retryButton", flags)?.GetValue(form) as Button;
            _volumeTrackBar = typeof(MainForm).GetField("_volumeTrackBar", flags)?.GetValue(form) as TrackBar;
            _legacyPlayer = typeof(MainForm).GetField("_mediaPlayer", flags)?.GetValue(form) as MediaPlayer;

            // Detach LibVLC from the visible VideoView. mpv owns this surface.
            try
            {
                _videoHost.GetType().GetProperty("MediaPlayer")?.SetValue(_videoHost, null);
            }
            catch
            {
            }

            // Important: MainForm still calls the old LibVLC PlayChannel method.
            // Give that player a hidden 1x1 native target so VLC can never create its
            // own "VLC (Direct3D11 output)" top-level window. We then stop it as soon
            // as the selected channel is observed by the coordinator.
            _legacySink = new Panel
            {
                Size = new Size(1, 1),
                Location = new Point(-100, -100),
                Visible = false
            };
            _form.Controls.Add(_legacySink);
            _legacySink.CreateControl();

            if (_legacyPlayer is not null)
            {
                try
                {
                    _legacyPlayer.Hwnd = _legacySink.Handle;
                    _legacyPlayer.Mute = true;
                    _legacyPlayer.Volume = 0;
                }
                catch
                {
                }
            }

            _volume = _volumeTrackBar?.Value ?? 70;

            if (_playPauseButton is not null)
            {
                _playPauseButton.Click += (_, _) => TogglePause();
            }

            if (_muteButton is not null)
            {
                _muteButton.Click += (_, _) => ToggleMute();
            }

            if (_retryButton is not null)
            {
                _retryButton.Click += (_, _) =>
                {
                    if (_currentChannelField.GetValue(_form) is Channel channel)
                    {
                        _ = SwitchChannelAsync(channel, force: true);
                    }
                };
            }

            if (_volumeTrackBar is not null)
            {
                _volumeTrackBar.ValueChanged += (_, _) =>
                {
                    _volume = _volumeTrackBar.Value;
                    SuppressLegacyPlayback();
                    _ = SendCommandWithRetryAsync(new object[] { "set_property", "volume", _volume });
                };
            }

            _form.FormClosed += (_, _) => Dispose();

            // Start mpv once and keep it alive. Channel changes use IPC loadfile and
            // never kill/relaunch the player, which makes switching much lighter.
            EnsureMpvStarted();

            _pollTimer = new System.Windows.Forms.Timer { Interval = 70 };
            _pollTimer.Tick += (_, _) => PollCurrentChannel();
            _pollTimer.Start();
        }

        private void PollCurrentChannel()
        {
            SuppressLegacyPlayback();

            if (_currentChannelField.GetValue(_form) is not Channel channel)
            {
                return;
            }

            var key = $"{channel.Name}|{channel.Url}";
            if (string.Equals(key, _activeKey, StringComparison.Ordinal))
            {
                return;
            }

            _activeKey = key;
            _ = SwitchChannelAsync(channel);
        }

        private async Task SwitchChannelAsync(Channel channel, bool force = false)
        {
            var key = $"{channel.Name}|{channel.Url}";
            if (!force && !string.Equals(key, _activeKey, StringComparison.Ordinal))
            {
                _activeKey = key;
            }

            var generation = Interlocked.Increment(ref _switchGeneration);
            SetStatus("Αλλαγή καναλιού...");
            SuppressLegacyPlayback();

            await _switchGate.WaitAsync();
            try
            {
                if (_disposed || generation != _switchGeneration)
                {
                    return;
                }

                if (!EnsureMpvStarted())
                {
                    return;
                }

                // EXACT Free-TV URL, verbatim. The same persistent mpv process simply
                // replaces the currently loaded stream; no process restart occurs.
                var loaded = await SendCommandWithRetryAsync(
                    new object[] { "loadfile", channel.Url, "replace" },
                    attempts: 18,
                    connectTimeoutMs: 250);

                if (!loaded || generation != _switchGeneration)
                {
                    if (!loaded)
                    {
                        SetStatus("Το mpv δεν απάντησε • δες data\\mpv.log");
                    }
                    return;
                }

                _paused = false;
                await SendCommandWithRetryAsync(new object[] { "set_property", "pause", false }, 3, 200);
                await SendCommandWithRetryAsync(new object[] { "set_property", "volume", _volume }, 3, 200);
                await SendCommandWithRetryAsync(new object[] { "set_property", "mute", _muted }, 3, 200);

                SafeUi(() =>
                {
                    if (_playPauseButton is not null) _playPauseButton.Text = "Ⅱ";
                    if (_muteButton is not null) _muteButton.Text = _muted ? "🔇" : "🔊";
                });

                SetStatus("Αναπαραγωγή μέσω mpv • αυτούσιο URL Free-TV");
            }
            finally
            {
                _switchGate.Release();
            }
        }

        private bool EnsureMpvStarted()
        {
            if (_disposed)
            {
                return false;
            }

            if (_process is not null && !_process.HasExited)
            {
                return true;
            }

            StopProcess();

            var mpvPath = Path.Combine(AppContext.BaseDirectory, "player", "mpv.exe");
            if (!File.Exists(mpvPath))
            {
                SetStatus("Λείπει το mpv.exe από το portable πακέτο.");
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "data"));
                var logPath = Path.Combine(AppContext.BaseDirectory, "data", "mpv.log");
                try { File.Delete(logPath); } catch { }

                var hwnd = _videoHost.Handle.ToInt64();
                _pipeName = $"dwrean-tv-mpv-{Guid.NewGuid():N}";
                var pipePath = $@"\\.\pipe\{_pipeName}";

                var psi = new ProcessStartInfo
                {
                    FileName = mpvPath,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add($"--wid={hwnd}");
                psi.ArgumentList.Add($"--input-ipc-server={pipePath}");
                psi.ArgumentList.Add("--no-config");
                psi.ArgumentList.Add("--no-terminal");
                psi.ArgumentList.Add("--osc=no");
                psi.ArgumentList.Add("--input-default-bindings=no");
                psi.ArgumentList.Add("--idle=yes");
                psi.ArgumentList.Add("--force-window=no");
                psi.ArgumentList.Add("--keep-open=yes");
                psi.ArgumentList.Add("--hwdec=auto-safe");
                psi.ArgumentList.Add("--cache=yes");
                psi.ArgumentList.Add("--demuxer-max-bytes=24MiB");
                psi.ArgumentList.Add("--demuxer-readahead-secs=6");
                psi.ArgumentList.Add($"--volume={_volume}");
                psi.ArgumentList.Add($"--log-file={logPath}");

                _process = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                var startedProcess = _process;
                startedProcess.Exited += (_, _) => SafeUi(() =>
                {
                    if (_disposed || !ReferenceEquals(_process, startedProcess))
                    {
                        return;
                    }

                    _activeKey = string.Empty;
                    _pipeName = null;
                    SetStatus("Ο mpv player τερματίστηκε. Θα γίνει αυτόματη επανεκκίνηση.");
                    if (_playPauseButton is not null) _playPauseButton.Text = "▶";
                });

                if (!startedProcess.Start())
                {
                    SetStatus("Δεν ήταν δυνατή η εκκίνηση του mpv.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                SetStatus($"Σφάλμα mpv: {ex.Message}");
                return false;
            }
        }

        private void TogglePause()
        {
            if (!EnsureMpvStarted())
            {
                return;
            }

            _paused = !_paused;
            _ = SendCommandWithRetryAsync(new object[] { "set_property", "pause", _paused });
            if (_playPauseButton is not null)
            {
                _playPauseButton.Text = _paused ? "▶" : "Ⅱ";
            }
            SetStatus(_paused ? "Παύση" : "Αναπαραγωγή μέσω mpv • αυτούσιο URL Free-TV");
            SuppressLegacyPlayback();
        }

        private void ToggleMute()
        {
            _muted = !_muted;
            _ = SendCommandWithRetryAsync(new object[] { "set_property", "mute", _muted });
            if (_muteButton is not null)
            {
                _muteButton.Text = _muted ? "🔇" : "🔊";
            }
            SuppressLegacyPlayback();
        }

        private async Task<bool> SendCommandWithRetryAsync(
            object[] command,
            int attempts = 8,
            int connectTimeoutMs = 220)
        {
            for (var attempt = 0; attempt < attempts && !_disposed; attempt++)
            {
                var pipeName = _pipeName;
                var process = _process;
                if (string.IsNullOrWhiteSpace(pipeName) || process is null || process.HasExited)
                {
                    return false;
                }

                try
                {
                    using var pipe = new NamedPipeClientStream(
                        ".",
                        pipeName,
                        PipeDirection.Out,
                        PipeOptions.Asynchronous);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(connectTimeoutMs));
                    await pipe.ConnectAsync(timeout.Token);

                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };

                    var payload = JsonSerializer.Serialize(new Dictionary<string, object[]>
                    {
                        ["command"] = command
                    });
                    await writer.WriteLineAsync(payload);
                    return true;
                }
                catch
                {
                    await Task.Delay(80);
                }
            }

            return false;
        }

        private void SuppressLegacyPlayback()
        {
            try
            {
                if (_legacyPlayer is null)
                {
                    return;
                }

                _legacyPlayer.Mute = true;
                _legacyPlayer.Volume = 0;
                _legacyPlayer.Hwnd = _legacySink.Handle;

                if (_legacyPlayer.IsPlaying)
                {
                    _legacyPlayer.Stop();
                }
            }
            catch
            {
            }
        }

        private void StopProcess()
        {
            var process = _process;
            _process = null;
            _pipeName = null;

            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private void SetStatus(string text)
        {
            SafeUi(() =>
            {
                if (_statusLabel is not null)
                {
                    _statusLabel.Text = text;
                }
            });
        }

        private void SafeUi(Action action)
        {
            if (_disposed || _form.IsDisposed)
            {
                return;
            }

            try
            {
                if (_form.InvokeRequired)
                {
                    _form.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pollTimer.Stop();
            _pollTimer.Dispose();
            StopProcess();
            _switchGate.Dispose();
            _legacySink.Dispose();
        }
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
            .Replace("0.2.1", CurrentVersion, StringComparison.Ordinal)
            .Replace("0.2.2", CurrentVersion, StringComparison.Ordinal)
            .Replace("0.2.3-test", CurrentVersion, StringComparison.Ordinal)
            .Replace("0.2.4-mpv-test", CurrentVersion, StringComparison.Ordinal);

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
        var flow = FindControl<FlowLayoutPanel>(form);
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
