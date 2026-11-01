using System.Collections.Concurrent;
using Chat.Api.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chat.Api.Tests;

public class EmailQueueTests
{
    /// <summary>A transport that waits for a release, then records the email, or fails for one address.</summary>
    private sealed class GatedSender : IEmailSender
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Sent { get; } = new();
        public TaskCompletionSource<string> Last { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Failing { get; init; }

        public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
        {
            await Release.Task.WaitAsync(ct);
            if (to == Failing) throw new IOException("smtp down");
            Sent.Enqueue(to);
            if (to == "last@example.test") Last.TrySetResult(to);
        }
    }

    [Fact]
    public async Task Enqueue_does_not_wait_for_the_mail_server_and_a_failed_email_does_not_stop_the_next()
    {
        var sender = new GatedSender { Failing = "broken@example.test" };
        var queue = new BackgroundEmailQueue(sender, NullLogger<BackgroundEmailQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);

        // The mail server is stuck, yet asking for the emails returns at once: a request never waits on SMTP.
        queue.Enqueue("broken@example.test", ("Subject", "Body 123456"));
        queue.Enqueue("last@example.test", ("Subject", "Body 654321"));
        Assert.Empty(sender.Sent);

        sender.Release.SetResult();
        await sender.Last.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["last@example.test"], sender.Sent);
        await queue.StopAsync(CancellationToken.None);
    }
}
