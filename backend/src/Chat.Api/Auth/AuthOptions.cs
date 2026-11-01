namespace Chat.Api.Auth;

public class AuthOptions
{
    public const string Section = "Auth";

    public string JwtKey { get; set; } = "";
    public string CodeKey { get; set; } = "";
    public string Issuer { get; set; } = "chat-api";
    public string Audience { get; set; } = "chat-clients";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int CodeMinutes { get; set; } = 15;
    public int CodeMaxAttempts { get; set; } = 5;
    public int ResendCooldownSeconds { get; set; } = 60;

    /// <summary>Calls to the anonymous auth endpoints allowed per client address and window (see <see cref="AuthRateLimit"/>).</summary>
    public int IpLimitCount { get; set; } = 30;

    public int IpLimitWindowSeconds { get; set; } = 300;

    public static bool IsValid(AuthOptions o) =>
        System.Text.Encoding.UTF8.GetByteCount(o.JwtKey) >= 32 && o.CodeKey.Length >= 16;
}

public class SmtpOptions
{
    public const string Section = "Smtp";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string From { get; set; } = "no-reply@chat.local";

    /// <summary>
    /// <c>Auto</c> uses TLS when the server offers it (Mailpit in development does not). Set <c>StartTls</c> or <c>SslOnConnect</c> for a
    /// real mail server, so a connection without TLS fails instead of sending in the clear.
    /// </summary>
    public MailKit.Security.SecureSocketOptions Security { get; set; } = MailKit.Security.SecureSocketOptions.Auto;

    /// <summary>Signs in to the mail server when set.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }
}
