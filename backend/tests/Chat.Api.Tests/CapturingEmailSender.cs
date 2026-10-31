using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Chat.Api.Email;

namespace Chat.Api.Tests;

public record SentEmail(string To, string Subject, string Body);

public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<SentEmail> _sent = new();

    public IReadOnlyList<SentEmail> Sent => _sent.ToArray();

    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        _sent.Enqueue(new SentEmail(to, subject, body));
        return Task.CompletedTask;
    }

    public IReadOnlyList<SentEmail> To(string email) =>
        Sent.Where(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The 6-digit code in the latest email sent to this address.</summary>
    public string LastCodeFor(string email)
    {
        var mail = To(email).LastOrDefault() ?? throw new InvalidOperationException($"No email sent to {email}.");
        return SixDigits().Match(mail.Body).Value;
    }

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();
}
