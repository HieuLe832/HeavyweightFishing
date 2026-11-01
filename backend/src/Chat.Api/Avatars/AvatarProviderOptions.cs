namespace Chat.Api.Avatars;

/// <summary>Where the optional Avatar Provider (the character pack) lives. An empty <see cref="BaseUrl"/> turns the feature off.</summary>
public class AvatarProviderOptions
{
    public const string Section = "AvatarProvider";

    public string? BaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 2;
    public int CatalogCacheSeconds { get; set; } = 60;

    /// <summary>Largest image accepted from the provider.</summary>
    public long MaxBytes { get; set; } = 2 * 1024 * 1024;
}
