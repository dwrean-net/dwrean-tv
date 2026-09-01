using System.Reflection;

namespace DwreanTv;

internal static class AppInfo
{
    public const string Name = "dwrean Ελληνική Τηλεόραση";
    public const string WebsiteUrl = "https://www.dwrean.net/";
    public const string SourcePageUrl = "https://github.com/Free-TV/IPTV/blob/master/lists/greece.md";

    public static string Version
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }
}
