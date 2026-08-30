using LibVLCSharp.Shared;

namespace DwreanTv;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Core.Initialize();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
