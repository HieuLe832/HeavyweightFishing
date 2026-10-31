namespace Chat.Api.Messaging;

public class MessagingOptions
{
    public const string Section = "Messaging";

    public int MaxBodyLength { get; set; } = 4000;
    public int MaxAttachments { get; set; } = 10;
    public int MaxEmojiLength { get; set; } = 32;
    public int RateLimitCount { get; set; } = 20;
    public int RateLimitWindowSeconds { get; set; } = 10;
    public int TypingLimitCount { get; set; } = 10;
    public int TypingLimitWindowSeconds { get; set; } = 10;
}
