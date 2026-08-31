using System.Drawing.Drawing2D;
using System.Reflection;
using LibVLCSharp.Shared;

namespace DwreanTv;

internal static class Program
{
    private const string CurrentVersion = "0.2.1";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

    [STAThread]
    private static void Main()
    {
        Core.Initialize();
        ApplicationConfiguration.Initialize();

        var mainForm = new MainForm();
        InstallPlaybackCompatibility(mainForm);
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

    private static void InstallPlaybackCompatibility(MainForm form)
    {
        var field = typeof(MainForm).GetField("_mediaPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(form) is not MediaPlayer player)
        {
            return;
        }

        player.MediaChanged += (_, e) =>
        {
            var media = e.Media;
            var mrl = media.Mrl ?? string.Empty;

            media.AddOption($":http-user-agent={BrowserUserAgent}");
            media.AddOption(":http-reconnect=true");
            media.AddOption(":network-caching=2500");
            media.AddOption(":live-caching=2500");

            if (mrl.Contains("antennaplus.gr", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=http://watch.antennaplus.gr");
            }
            else if (mrl.Contains("alphatvlive", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.alphatv.gr/");
            }
            else if (mrl.Contains("livestar.siliconweb.com", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.star.gr/");
            }
            else if (mrl.Contains("skai-live", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.skai.gr/");
            }
            else if (mrl.Contains("liveopen", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.tvopen.gr/");
            }
            else if (mrl.Contains("msvdn.net", StringComparison.OrdinalIgnoreCase))
            {
                media.AddOption(":http-referrer=https://www.megatv.com/");
            }
        };
    }

    private static void ApplyFinalPolish(Form form)
    {
        PolishHeader(form);
        PolishChannelList(form);
        RefreshDisplayedVersion(form);
    }

    private static void RefreshDisplayedVersion(Control root)
    {
        if (root.Text.Contains("0.2.0", StringComparison.Ordinal))
        {
            root.Text = root.Text.Replace("0.2.0", CurrentVersion, StringComparison.Ordinal);
        }

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
