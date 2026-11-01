using System.Threading.Channels;

namespace Chat.Api.Email;

/// <summary>Emails a request wants sent. The request does not wait for the mail server (see <see cref="BackgroundEmailQueue"/>).</summary>
public interface IEmailQueue
{
    void Enqueue(string to, (string Subject, string Body) mail);
}

/// <summary>
/// Sends queued emails one by one in the background. A request that waited for SMTP would answer slower for an address that exists
/// than for one that does not, which tells a stranger who has an account, and a slow mail server would hold the request. A failed or
/// dropped email is logged without its address (personal data); the user can ask for a new code. The queue lives in memory, so a
/// restart loses what has not been sent yet, for the same reason that is acceptable.
/// </summary>
public sealed class BackgroundEmailQueue(IEmailSender sender, ILogger<BackgroundEmailQueue> log) : BackgroundService, IEmailQueue
{
    private const int Capacity = 1000;

    private readonly Channel<(string To, string Subject, string Body)> _queue = Channel.CreateBounded<(string, string, string)>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public void Enqueue(string to, (string Subject, string Body) mail)
    {
        if (!_queue.Writer.TryWrite((to, mail.Subject, mail.Body)))
            log.LogError("The email queue is full ({Capacity}); an email was dropped", Capacity);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (to, subject, body) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await sender.SendAsync(to, subject, body, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Could not send an email ({Subject})", subject);
            }
        }
    }
}
