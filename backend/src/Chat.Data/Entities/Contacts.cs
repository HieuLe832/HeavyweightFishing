namespace Chat.Data.Entities;

// D4 Contacts & Blocks

public class Contact
{
    public Guid OwnerId { get; set; }
    public Guid ContactId { get; set; }
    public string? Nickname { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Block
{
    public Guid BlockerId { get; set; }
    public Guid BlockedId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
