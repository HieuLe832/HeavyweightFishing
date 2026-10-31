namespace Chat.Api.Moderation;

public class ModerationOptions
{
    public const string Section = "Moderation";

    public int ReportLimitCount { get; set; } = 10;
    public int ReportLimitWindowSeconds { get; set; } = 3600;
    public int MaxDetailsLength { get; set; } = 1000;
}
