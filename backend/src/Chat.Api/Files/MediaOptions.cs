namespace Chat.Api.Files;

public class MediaOptions
{
    public const string Section = "Media";

    /// <summary>Largest file accepted. Files are held in memory while they are checked, so keep this modest.</summary>
    public long MaxBytes { get; set; } = 10 * 1024 * 1024;

    public int UploadLimitCount { get; set; } = 20;
    public int UploadLimitWindowSeconds { get; set; } = 60;
}
