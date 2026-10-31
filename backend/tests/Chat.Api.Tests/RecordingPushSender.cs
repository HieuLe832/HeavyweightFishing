using System.Collections.Concurrent;
using Chat.Api.Push;
using Chat.Data;

namespace Chat.Api.Tests;

public record SentPush(PushPlatform Platform, string Token, PushMessage Message);

/// <summary>Stands in for the push providers: records what it was asked to send and can answer "invalid token" or fail.</summary>
public sealed class RecordingPushSender
{
    private readonly ConcurrentQueue<SentPush> _sent = new();

    public IReadOnlyCollection<SentPush> Sent => _sent.ToArray();
    public ConcurrentDictionary<string, PushResult> Results { get; } = new();
    public ConcurrentDictionary<string, bool> Failing { get; } = new();

    public IPushSender For(PushPlatform platform) => new PlatformSender(this, platform);

    public IReadOnlyList<SentPush> To(string token) => _sent.Where(s => s.Token == token).ToList();

    private sealed class PlatformSender(RecordingPushSender owner, PushPlatform platform) : IPushSender
    {
        public PushPlatform Platform => platform;

        public Task<PushResult> SendAsync(PushMessage message, string token, CancellationToken ct)
        {
            if (owner.Failing.ContainsKey(token)) throw new HttpRequestException("provider down");
            owner._sent.Enqueue(new SentPush(platform, token, message));
            return Task.FromResult(owner.Results.GetValueOrDefault(token, PushResult.Sent));
        }
    }
}
