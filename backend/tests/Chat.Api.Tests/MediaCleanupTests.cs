using Chat.Api.Files;
using Chat.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

internal static class CleanupHelpers
{
    public static MediaCleanupService NewCleanup(this ApiFixture api, MediaCleanupOptions? options = null, IObjectStore? store = null) =>
        ActivatorUtilities.CreateInstance<MediaCleanupService>(api.Services, Options.Create(options ?? new MediaCleanupOptions()), store ?? api.Services.GetRequiredService<InMemoryObjectStore>());

    public static Task<bool> MediaExistsAsync(this ApiFixture api, Guid id) => api.WithDbAsync(db => db.Media.AnyAsync(m => m.Id == id));

    public static Task<string> StorageKeyAsync(this ApiFixture api, Guid id) => api.WithDbAsync(db => db.Media.Where(m => m.Id == id).Select(m => m.StorageKey).SingleAsync());

    public static async Task<bool> BytesExistAsync(this ApiFixture api, string key) =>
        await api.Services.GetRequiredService<InMemoryObjectStore>().GetAsync(key, CancellationToken.None) is not null;

    public static void Age(this ApiFixture api, int days = 8) => api.Time.Advance(TimeSpan.FromDays(days));

    public static async Task<(Person Alice, Person Bob, Guid Chat, HubConnection AliceHub)> ChatAsync(this ApiFixture api)
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        return (alice, bob, await api.OpenDirectAsync(alice, bob), await api.ConnectAsync(alice));
    }

    public static async Task<(Guid MessageId, Guid MediaId)> SentFileAsync(this ApiFixture api, Person sender, HubConnection hub, Guid chat)
    {
        var media = await api.UploadedAsync(sender);
        var message = await hub.SendMediaAsync(chat, [media]);
        return (message.GetProperty("id").GetGuid(), media);
    }
}

public class MediaCleanupTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task An_old_unattached_upload_is_removed_with_its_bytes()
    {
        var alice = await api.CreatePersonAsync();
        var media = await api.UploadedAsync(alice);
        var key = await api.StorageKeyAsync(media);
        api.Age();

        await api.NewCleanup().RunOnceAsync();

        Assert.False(await api.MediaExistsAsync(media));
        Assert.False(await api.BytesExistAsync(key));
    }

    [Fact]
    public async Task An_upload_inside_the_grace_period_is_kept()
    {
        var alice = await api.CreatePersonAsync();
        var media = await api.UploadedAsync(alice);
        var key = await api.StorageKeyAsync(media);
        api.Age(6);

        await api.NewCleanup().RunOnceAsync();

        Assert.True(await api.MediaExistsAsync(media));
        Assert.True(await api.BytesExistAsync(key));
    }

    [Fact]
    public async Task A_user_avatar_is_kept_and_a_replaced_avatar_is_removed()
    {
        var alice = await api.CreatePersonAsync();
        var first = await api.UploadedAsync(alice);
        var second = await api.UploadedAsync(alice);
        (await api.Client.PutAsync(alice, "/users/me/avatar", new { mediaId = first })).EnsureSuccessStatusCode();
        (await api.Client.PutAsync(alice, "/users/me/avatar", new { mediaId = second })).EnsureSuccessStatusCode();
        api.Age();

        await api.NewCleanup().RunOnceAsync();

        Assert.False(await api.MediaExistsAsync(first));
        Assert.True(await api.MediaExistsAsync(second));
    }

    [Fact]
    public async Task A_group_avatar_is_kept()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var group = await api.CreateGroupAsync(alice, bob);
        var media = await api.UploadedAsync(alice);
        (await api.Client.PutAsync(alice, $"/conversations/{group}/avatar", new { mediaId = media })).EnsureSuccessStatusCode();
        api.Age();

        await api.NewCleanup().RunOnceAsync();

        Assert.True(await api.MediaExistsAsync(media));
    }

    [Fact]
    public async Task A_file_on_a_live_message_is_kept()
    {
        var (alice, _, chat, hub) = await api.ChatAsync();
        var (_, media) = await api.SentFileAsync(alice, hub, chat);
        api.Age();

        await api.NewCleanup().RunOnceAsync();

        Assert.True(await api.MediaExistsAsync(media));
    }

    [Fact]
    public async Task A_file_on_a_live_message_is_kept_after_its_uploader_left_the_group()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        var group = await api.CreateGroupAsync(alice, bob);
        var hub = await api.ConnectAsync(bob);
        var (_, media) = await api.SentFileAsync(bob, hub, group);
        (await api.Client.PostAsync(bob, $"/conversations/{group}/leave")).EnsureSuccessStatusCode();
        api.Age();

        await api.NewCleanup().RunOnceAsync();

        Assert.True(await api.MediaExistsAsync(media));
    }

    [Fact]
    public async Task A_file_only_on_a_deleted_message_is_removed_with_its_attachment_rows()
    {
        var (alice, _, chat, hub) = await api.ChatAsync();
        var (message, media) = await api.SentFileAsync(alice, hub, chat);
        await hub.InvokeAsync<System.Text.Json.JsonElement>("DeleteMessage", message);
        api.Age();

        await api.NewCleanup().RunOnceAsync();

        Assert.False(await api.MediaExistsAsync(media));
        Assert.False(await api.WithDbAsync(db => db.MessageAttachments.AnyAsync(a => a.MediaId == media)));
    }

    [Fact]
    public async Task A_file_on_a_deleted_message_that_was_reported_is_kept()
    {
        var (alice, bob, chat, hub) = await api.ChatAsync();
        var (message, media) = await api.SentFileAsync(alice, hub, chat);
        await api.ReportedMessageAsync(bob, message);
        await hub.InvokeAsync<System.Text.Json.JsonElement>("DeleteMessage", message);
        api.Age();

        await api.NewCleanup().RunOnceAsync();

        Assert.True(await api.MediaExistsAsync(media));
        Assert.True(await api.WithDbAsync(db => db.MessageAttachments.AnyAsync(a => a.MediaId == media)));
    }

    [Fact]
    public async Task A_file_on_both_a_deleted_and_a_live_message_is_kept_with_both_rows()
    {
        var (alice, _, chat, hub) = await api.ChatAsync();
        var media = await api.UploadedAsync(alice);
        var dead = (await hub.SendMediaAsync(chat, [media])).GetProperty("id").GetGuid();
        await hub.SendMediaAsync(chat, [media]);
        await hub.InvokeAsync<System.Text.Json.JsonElement>("DeleteMessage", dead);
        api.Age();

        await api.NewCleanup().RunOnceAsync();

        Assert.True(await api.MediaExistsAsync(media));
        Assert.Equal(2, await api.WithDbAsync(db => db.MessageAttachments.CountAsync(a => a.MediaId == media)));
    }

    [Theory]
    [InlineData(MediaStatus.Pending)]
    [InlineData(MediaStatus.Rejected)]
    public async Task Pending_and_rejected_old_uploads_are_removed(MediaStatus status)
    {
        var alice = await api.CreatePersonAsync();
        var media = await api.UploadedAsync(alice);
        await api.WithDbAsync(db => db.Media.Where(m => m.Id == media).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, status)));
        api.Age();

        await api.NewCleanup().RunOnceAsync();

        Assert.False(await api.MediaExistsAsync(media));
    }
}

/// <summary>Each test gets its own database and clock, so the number of removed files can be asserted exactly.</summary>
public class MediaCleanupCountTests : IAsyncLifetime
{
    private readonly ApiFixture _api = new();

    public Task InitializeAsync() => _api.InitializeAsync();
    public Task DisposeAsync() => _api.DisposeAsync();

    private async Task<List<Guid>> UploadsAsync(int count)
    {
        var alice = await _api.CreatePersonAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++) ids.Add(await _api.UploadedAsync(alice, TestFiles.Text($"file {i}\n")));
        return ids;
    }

    [Fact]
    public async Task Nothing_to_clean_returns_zero()
    {
        _api.Age();

        Assert.Equal(0, await _api.NewCleanup().RunOnceAsync());
    }

    [Fact]
    public async Task More_candidates_than_BatchSize_are_all_removed_in_one_run()
    {
        var ids = await UploadsAsync(5);
        _api.Age();

        var removed = await _api.NewCleanup(new MediaCleanupOptions { BatchSize = 2 }).RunOnceAsync();

        Assert.Equal(5, removed);
        foreach (var id in ids) Assert.False(await _api.MediaExistsAsync(id));
    }

    [Fact]
    public async Task A_failing_object_store_delete_still_removes_the_other_files_and_all_rows()
    {
        var ids = await UploadsAsync(3);
        var failingKey = await _api.StorageKeyAsync(ids[1]);
        _api.Age();

        var removed = await _api.NewCleanup(store: new ThrowingObjectStore(_api.Services.GetRequiredService<InMemoryObjectStore>(), failingKey)).RunOnceAsync();

        Assert.Equal(3, removed);
        foreach (var id in ids) Assert.False(await _api.MediaExistsAsync(id));
        Assert.True(await _api.BytesExistAsync(failingKey));
    }

    [Fact]
    public async Task Two_runs_at_once_remove_each_file_once()
    {
        var ids = await UploadsAsync(6);
        _api.Age();

        var results = await Task.WhenAll(
            _api.NewCleanup(new MediaCleanupOptions { BatchSize = 2 }).RunOnceAsync(),
            _api.NewCleanup(new MediaCleanupOptions { BatchSize = 2 }).RunOnceAsync());

        Assert.Equal(6, results.Sum());
        foreach (var id in ids) Assert.False(await _api.MediaExistsAsync(id));
    }

    [Fact]
    public async Task The_timer_removes_unused_files_when_enabled()
    {
        var media = (await UploadsAsync(1))[0];
        _api.Age();
        var service = _api.NewCleanup(new MediaCleanupOptions { Enabled = true, IntervalMinutes = 1 });
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () => !await _api.MediaExistsAsync(media), tick: true);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_disabled_service_never_runs()
    {
        var media = (await UploadsAsync(1))[0];
        _api.Age();
        var service = _api.NewCleanup(new MediaCleanupOptions { Enabled = false, IntervalMinutes = 1 });
        await service.StartAsync(CancellationToken.None);
        _api.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        Assert.True(await _api.MediaExistsAsync(media));
    }

    [Fact]
    public async Task A_failing_tick_does_not_stop_the_loop()
    {
        var media = (await UploadsAsync(1))[0];
        _api.Age();
        var failing = new FailOnceObjectStore(_api.Services.GetRequiredService<InMemoryObjectStore>());
        var service = _api.NewCleanup(new MediaCleanupOptions { Enabled = true, IntervalMinutes = 1, BatchSize = 1 }, failing);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // The first tick throws while listing; the second must still run.
            failing.Armed = true;
            await WaitUntilAsync(() => Task.FromResult(failing.Failures >= 1), tick: true);
            failing.Armed = false;
            await WaitUntilAsync(async () => !await _api.MediaExistsAsync(media), tick: true);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The service starts its timer a moment after StartAsync returns, so with <paramref name="tick"/> the clock keeps advancing a minute at a time.</summary>
    private async Task WaitUntilAsync(Func<Task<bool>> condition, bool tick = false)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within 5 seconds");
            if (tick) _api.Time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(25);
        }
    }

    private sealed class FailOnceObjectStore(IObjectStore inner) : IObjectStore
    {
        public bool Armed { get; set; }
        public int Failures { get; private set; }

        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct) => inner.PutAsync(key, content, contentType, ct);
        public Task<byte[]?> GetAsync(string key, CancellationToken ct) => inner.GetAsync(key, ct);

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            if (!Armed) return inner.DeleteAsync(key, ct);
            Failures++;
            throw new OperationCanceledException("not the caller's token"); // escapes the per-key catch, like a broken tick
        }
    }

    private sealed class ThrowingObjectStore(IObjectStore inner, string failingKey) : IObjectStore
    {
        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct) => inner.PutAsync(key, content, contentType, ct);
        public Task<byte[]?> GetAsync(string key, CancellationToken ct) => inner.GetAsync(key, ct);

        public Task DeleteAsync(string key, CancellationToken ct) =>
            key == failingKey ? throw new HttpRequestException("object store down") : inner.DeleteAsync(key, ct);
    }
}
