using LibVLCSharp.Shared;

namespace DwreanTv;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Core.Initialize();
        ApplicationConfiguration.Initialize();

        var mainForm = new MainForm();
        FixHeaderSpacing(mainForm);
        Application.Run(mainForm);
    }

    private static void FixHeaderSpacing(Form form)
    {
        var header = form.Controls
            .OfType<Panel>()
            .FirstOrDefault(panel => panel.Dock == DockStyle.Top);

        if (header is null)
        {
            return;
        }

        header.Height = 88;

        var logo = header.Controls.OfType<PictureBox>().FirstOrDefault();
        if (logo is not null)
        {
            logo.Location = new Point(28, 18);
            logo.Size = new Size(48, 48);
        }

        var dwrean = header.Controls
            .OfType<Label>()
            .FirstOrDefault(label => label.Text == "dwrean");
        if (dwrean is not null)
        {
            dwrean.Location = new Point(88, 15);
        }

        var title = header.Controls
            .OfType<Label>()
            .FirstOrDefault(label => label.Text == "Ελληνική Τηλεόραση");
        if (title is not null)
        {
            title.Location = new Point(174, 15);
        }

        var subtitle = header.Controls
            .OfType<Label>()
            .FirstOrDefault(label => label.Text.StartsWith("Δωρεάν ελληνικά", StringComparison.Ordinal));
        if (subtitle is not null)
        {
            subtitle.Location = new Point(90, 50);
        }
    }
}
