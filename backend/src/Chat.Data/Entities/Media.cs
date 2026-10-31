namespace Chat.Data.Entities;

// D7 Media Files (bytes live in object storage)

public class Media
{
    public Guid Id { get; set; }
    public Guid UploaderId { get; set; }
    public required string StorageKey { get; set; }
    public required string MimeType { get; set; }
    public long SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public required byte[] Sha256 { get; set; }
    public MediaStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
