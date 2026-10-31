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

    public static bool IsValid(AuthOptions o) =>
        System.Text.Encoding.UTF8.GetByteCount(o.JwtKey) >= 32 && o.CodeKey.Length >= 16;
}

public class SmtpOptions
{
    public const string Section = "Smtp";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string From { get; set; } = "no-reply@chat.local";
}
