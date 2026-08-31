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
    private const string CurrentVersion = "0.2.4-mpv-test";

    [STAThread]
    private static void Main()
    {
        // MainForm still contains the legacy LibVLC controls, so its native runtime
        // must be initialized. Actual TV playback in this test is handled by mpv.
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
        private readonly System.Windows.Forms.Timer _pollTimer;

        private Process? _process;
        private string? _pipeName;
        private string _activeKey = string.Empty;
        private bool _paused;
        private bool _muted;
        private int _volume;
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

            // Detach LibVLC from the visible surface. The MainForm stays intact, but
            // mpv owns the actual video output for this test.
            try
            {
                _videoHost.GetType().GetProperty("MediaPlayer")?.SetValue(_videoHost, null);
            }
            catch
            {
            }

            if (_legacyPlayer is not null)
            {
                _legacyPlayer.Mute = true;
                _legacyPlayer.Volume = 0;
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
                        StartChannel(channel, force: true);
                    }
                };
            }

            if (_volumeTrackBar is not null)
            {
                _volumeTrackBar.ValueChanged += (_, _) =>
                {
                    _volume = _volumeTrackBar.Value;
                    KeepLegacySilent();
                    _ = SendCommandAsync("set_property", "volume", _volume);
                };
            }

            _form.FormClosed += (_, _) => Dispose();

            _pollTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _pollTimer.Tick += (_, _) => PollCurrentChannel();
            _pollTimer.Start();
        }

        private void PollCurrentChannel()
        {
            KeepLegacySilent();

            if (_currentChannelField.GetValue(_form) is not Channel channel)
            {
                return;
            }

            var key = $"{channel.Name}|{channel.Url}";
            if (string.Equals(key, _activeKey, StringComparison.Ordinal))
            {
                return;
            }

            StartChannel(channel);
        }

        private void StartChannel(Channel channel, bool force = false)
        {
            var key = $"{channel.Name}|{channel.Url}";
            if (!force && string.Equals(key, _activeKey, StringComparison.Ordinal))
            {
                return;
            }

            _activeKey = key;
            StopProcess();
            KeepLegacySilent();

            var mpvPath = Path.Combine(AppContext.BaseDirectory, "player", "mpv.exe");
            if (!File.Exists(mpvPath))
            {
                SetStatus("Λείπει το mpv.exe από το portable πακέτο.");
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "data"));
                var logPath = Path.Combine(AppContext.BaseDirectory, "data", "mpv.log");
                try { File.Delete(logPath); } catch { }

                // mpv's --wid accepts a Win32 HWND. The stream URL below is passed
                // as a separate argument and is NEVER rewritten or supplemented.
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
                psi.ArgumentList.Add("--force-window=yes");
                psi.ArgumentList.Add("--keep-open=no");
                psi.ArgumentList.Add("--hwdec=auto-safe");
                psi.ArgumentList.Add("--cache=yes");
                psi.ArgumentList.Add("--demuxer-max-bytes=64MiB");
                psi.ArgumentList.Add("--demuxer-readahead-secs=15");
                psi.ArgumentList.Add($"--volume={_volume}");
                psi.ArgumentList.Add($"--log-file={logPath}");
                psi.ArgumentList.Add(channel.Url); // EXACT Free-TV URL, verbatim.

                _process = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };
                _process.Exited += (_, _) => SafeUi(() =>
                {
                    if (!_disposed && _process is not null && _process.HasExited)
                    {
                        if (_statusLabel is not null &&
                            !_statusLabel.Text.Contains("Αναπαραγωγή", StringComparison.OrdinalIgnoreCase))
                        {
                            _statusLabel.Text = "Το mpv δεν μπόρεσε να ανοίξει το stream • δες data\\mpv.log";
                        }
                        if (_playPauseButton is not null)
                        {
                            _playPauseButton.Text = "▶";
                        }
                    }
                });

                if (!_process.Start())
                {
                    SetStatus("Δεν ήταν δυνατή η εκκίνηση του mpv.");
                    return;
                }

                _paused = false;
                _muted = false;
                SetStatus("Σύνδεση μέσω mpv • αυτούσιο URL Free-TV...");
                if (_playPauseButton is not null) _playPauseButton.Text = "Ⅱ";
                if (_muteButton is not null) _muteButton.Text = "🔊";

                var expectedProcess = _process;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    SafeUi(() =>
                    {
                        if (!_disposed && ReferenceEquals(_process, expectedProcess) &&
                            expectedProcess is not null && !expectedProcess.HasExited)
                        {
                            SetStatus("Αναπαραγωγή μέσω mpv • αυτούσιο URL Free-TV");
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Σφάλμα mpv: {ex.Message}");
            }
        }

        private void TogglePause()
        {
            if (_process is null || _process.HasExited)
            {
                if (_currentChannelField.GetValue(_form) is Channel channel)
                {
                    StartChannel(channel, force: true);
                }
                return;
            }

            _paused = !_paused;
            _ = SendCommandAsync("set_property", "pause", _paused);
            if (_playPauseButton is not null)
            {
                _playPauseButton.Text = _paused ? "▶" : "Ⅱ";
            }
            SetStatus(_paused ? "Παύση" : "Αναπαραγωγή μέσω mpv • αυτούσιο URL Free-TV");
            KeepLegacySilent();
        }

        private void ToggleMute()
        {
            _muted = !_muted;
            _ = SendCommandAsync("set_property", "mute", _muted);
            if (_muteButton is not null)
            {
                _muteButton.Text = _muted ? "🔇" : "🔊";
            }
            KeepLegacySilent();
        }

        private async Task SendCommandAsync(params object[] command)
        {
            var pipeName = _pipeName;
            var process = _process;
            if (string.IsNullOrWhiteSpace(pipeName) || process is null || process.HasExited)
            {
                return;
            }

            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
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
            }
            catch
            {
                // IPC failure must not crash the TV application.
            }
        }

        private void KeepLegacySilent()
        {
            try
            {
                if (_legacyPlayer is not null)
                {
                    _legacyPlayer.Mute = true;
                    _legacyPlayer.Volume = 0;
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
                    process.WaitForExit(800);
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
            .Replace("0.2.3-test", CurrentVersion, StringComparison.Ordinal);

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
