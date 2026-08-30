namespace DwreanTv.Models;

public sealed class Channel
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string LogoUrl { get; set; } = string.Empty;
    public string EpgId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool GeoBlocked { get; set; }
    public bool IsYouTube { get; set; }

    public override string ToString() => Name;
}
