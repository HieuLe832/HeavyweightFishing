namespace Chat.Api;

/// <summary>Which browser origins may call the API and the chat hub. Empty means none (the default outside Development).</summary>
public class CorsOptions
{
    public const string Section = "Cors";
    public const string PolicyName = "web";

    public string[] AllowedOrigins { get; set; } = [];
}
