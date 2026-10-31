namespace Chat.Api.Files;

public class MediaCleanupOptions
{
    public const string Section = "Media:Cleanup";

    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>A file must be this old before it can be removed: an upload may still be waiting for its message.</summary>
    public int GraceDays { get; set; } = 7;

    public int BatchSize { get; set; } = 200;
}
