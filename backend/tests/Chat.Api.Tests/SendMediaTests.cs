using System.Net;
using System.Text.Json;
using Chat.Api.Push;
using Chat.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.HubTestHelpers;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public class SendMediaTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private async Task<(Person Alice, Person Bob, Guid Chat, HubConnection AliceHub, HubConnection BobHub)> DirectChatAsync()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync();
        return (alice, bob, await api.OpenDirectAsync(alice, bob), await api.ConnectAsync(alice), await api.ConnectAsync(bob));
    }

    private static Guid[] AttachmentIds(JsonElement message) =>
        message.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("mediaId").GetGuid()).ToArray();

    [Fact]
    public async Task The_ack_and_the_event_carry_the_caption_and_the_attachments_in_order()
    {
        var (alice, bob, chat, aliceHub, bobHub) = await DirectChatAsync();
        var bobInbox = Inbox.Listen(bobHub);
        var photo = await api.UploadedAsync(alice, TestFiles.Png(640, 480));
        var doc = await api.UploadedAsync(alice, TestFiles.Pdf());

        var sent = await aliceHub.SendMediaAsync(chat, [doc, photo], "  holiday  ");

        Assert.Equal("media", sent.GetProperty("type").GetString());
        Assert.Equal("holiday", sent.GetProperty("body").GetString());
        Assert.Equal(1, sent.GetProperty("seq").GetInt64());
        Assert.Equal([doc, photo], AttachmentIds(sent));
        var image = sent.GetProperty("attachments")[1];
        Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        Assert.Equal(640, image.GetProperty("width").GetInt32());
        Assert.Equal(480, image.GetProperty("height").GetInt32());
        Assert.Equal([doc, photo], AttachmentIds(await bobInbox.Next("message.created")));
        Assert.Equal(HttpStatusCode.OK, (await api.DownloadAsync(bob, photo)).StatusCode);
    }

    [Fact]
    public async Task A_caption_is_optional_and_a_file_can_be_attached_more_than_once()
    {
        var (alice, _, chat, aliceHub, _) = await DirectChatAsync();
        var photo = await api.UploadedAsync(alice);

        var first = await aliceHub.SendMediaAsync(chat, [photo]);
        var second = await aliceHub.SendMediaAsync(chat, [photo], "   ");

        Assert.Equal(JsonValueKind.Null, first.GetProperty("body").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("body").ValueKind);
        Assert.Equal([photo], AttachmentIds(second));
    }

    [Fact]
    public async Task A_retry_returns_the_original_with_its_attachments_and_broadcasts_once()
    {
        var (alice, _, chat, aliceHub, bobHub) = await DirectChatAsync();
        var bobInbox = Inbox.Listen(bobHub);
        var photo = await api.UploadedAsync(alice);
        var clientMsgId = Guid.NewGuid();

        var first = await aliceHub.SendMediaAsync(chat, [photo], "once", clientMsgId);
        var retry = await aliceHub.SendMediaAsync(chat, [photo], "once", clientMsgId);

        Assert.Equal(first.GetProperty("id").GetGuid(), retry.GetProperty("id").GetGuid());
        Assert.Equal([photo], AttachmentIds(retry));
        await bobInbox.Next("message.created");
        await bobInbox.ExpectNone("message.created");
    }

    [Fact]
    public async Task History_shows_the_attachments_and_a_deleted_message_shows_none()
    {
        var (alice, bob, chat, aliceHub, _) = await DirectChatAsync();
        var photo = await api.UploadedAsync(alice);
        var sent = await aliceHub.SendMediaAsync(chat, [photo], "look");
        await SendMessageTests.SendAsync(aliceHub, chat, "and some text");

        var page = await (await api.Client.GetAsync(bob, $"/conversations/{chat}/messages")).JsonAsync();
        var messages = page.GetProperty("messages");
        Assert.Equal([photo], AttachmentIds(messages[0]));
        Assert.Empty(AttachmentIds(messages[1]));

        var deleted = await aliceHub.InvokeAsync<JsonElement>("DeleteMessage", sent.GetProperty("id").GetGuid());
        Assert.Empty(AttachmentIds(deleted));
        page = await (await api.Client.GetAsync(bob, $"/conversations/{chat}/messages")).JsonAsync();
        Assert.Empty(AttachmentIds(page.GetProperty("messages")[0]));
    }

    [Fact]
    public async Task Editing_a_media_message_changes_the_caption_and_keeps_the_attachments()
    {
        var (alice, _, chat, aliceHub, _) = await DirectChatAsync();
        var photo = await api.UploadedAsync(alice);
        var sent = await aliceHub.SendMediaAsync(chat, [photo], "typo");

        var edited = await aliceHub.InvokeAsync<JsonElement>("EditMessage", sent.GetProperty("id").GetGuid(), "fixed");

        Assert.Equal("fixed", edited.GetProperty("body").GetString());
        Assert.Equal([photo], AttachmentIds(edited));
    }

    [Fact]
    public async Task Files_that_are_missing_someone_elses_or_not_ready_are_invalid_attachments()
    {
        var (alice, bob, chat, aliceHub, _) = await DirectChatAsync();
        var mine = await api.UploadedAsync(alice);
        var bobs = await api.UploadedAsync(bob);
        var pending = await api.UploadedAsync(alice);
        var rejected = await api.UploadedAsync(alice);
        await api.WithDbAsync(db => db.Media.Where(m => m.Id == pending).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MediaStatus.Pending)));
        await api.WithDbAsync(db => db.Media.Where(m => m.Id == rejected).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MediaStatus.Rejected)));

        foreach (var bad in new[] { Guid.NewGuid(), bobs, pending, rejected })
            await AssertHubErrorAsync(() => aliceHub.SendMediaAsync(chat, [mine, bad]), "invalid_attachment");
        Assert.Equal(0, await api.WithDbAsync(db => db.Messages.CountAsync(m => m.ConversationId == chat)));
    }

    [Fact]
    public async Task The_number_of_files_and_the_caption_are_checked()
    {
        var (alice, _, chat, aliceHub, _) = await DirectChatAsync();
        var one = await api.UploadedAsync(alice);
        var many = new List<Guid>();
        for (var i = 0; i < 11; i++) many.Add(await api.UploadedAsync(alice, TestFiles.Text($"file {i}")));

        await AssertHubErrorAsync(() => aliceHub.SendMediaAsync(chat, []), "validation_failed");
        await AssertHubErrorAsync(() => aliceHub.SendMediaAsync(chat, many), "validation_failed");
        await AssertHubErrorAsync(() => aliceHub.SendMediaAsync(chat, [one, one]), "validation_failed");
        await AssertHubErrorAsync(() => aliceHub.SendMediaAsync(chat, [one], new string('c', 4001)), "validation_failed");
        await AssertHubErrorAsync(() => aliceHub.SendMediaAsync(chat, [one], clientMsgId: Guid.Empty), "validation_failed");
        Assert.Equal(10, AttachmentIds(await aliceHub.SendMediaAsync(chat, many.Take(10))).Length); // ten is the limit
    }

    [Fact]
    public async Task Non_members_and_blocked_chats_are_refused_like_text_messages()
    {
        var (alice, bob, chat, aliceHub, bobHub) = await DirectChatAsync();
        var carol = await api.CreatePersonAsync();
        await using var carolHub = await api.ConnectAsync(carol);
        var photo = await api.UploadedAsync(alice);
        var carolsPhoto = await api.UploadedAsync(carol);
        (await api.Client.PutAsync(alice, $"/blocks/{bob.Id}")).EnsureSuccessStatusCode();

        await AssertHubErrorAsync(() => carolHub.SendMediaAsync(chat, [carolsPhoto]), "conversation_not_found");
        await AssertHubErrorAsync(() => aliceHub.SendMediaAsync(chat, [photo]), "blocked");
        var bobsPhoto = await api.UploadedAsync(bob);
        await AssertHubErrorAsync(() => bobHub.SendMediaAsync(chat, [bobsPhoto]), "user_not_found");
    }

    [Fact]
    public async Task A_stale_session_is_refused()
    {
        var (alice, _, chat, aliceHub, _) = await DirectChatAsync();
        var photo = await api.UploadedAsync(alice);
        (await api.Client.PostAsync(alice, "/auth/logout")).EnsureSuccessStatusCode();

        await AssertHubErrorAsync(() => aliceHub.SendMediaAsync(chat, [photo]), "session_ended");
    }

    [Fact]
    public async Task A_reply_must_point_into_the_same_conversation()
    {
        var (alice, _, chat, aliceHub, _) = await DirectChatAsync();
        var elsewhere = await api.OpenDirectAsync(alice, await api.CreatePersonAsync());
        var other = await SendMessageTests.SendAsync(aliceHub, elsewhere, "elsewhere");
        var photo = await api.UploadedAsync(alice);

        await AssertHubErrorAsync(() => aliceHub.SendMediaAsync(chat, [photo], replyToId: other.GetProperty("id").GetGuid()), "validation_failed");
    }

    [Fact]
    public async Task An_offline_member_is_pushed_the_caption_or_a_generic_line()
    {
        var alice = await api.CreatePersonAsync();
        var bob = await api.CreatePersonAsync(); // never connects, so offline
        var chat = await api.OpenDirectAsync(alice, bob);
        await using var aliceHub = await api.ConnectAsync(alice);
        var token = "tok-" + Guid.NewGuid().ToString("N");
        (await api.Client.PutAsync(bob, "/auth/sessions/current/push-token", new { platform = "fcm", token })).EnsureSuccessStatusCode();
        var photo = await api.UploadedAsync(alice);

        await aliceHub.SendMediaAsync(chat, [photo]);
        await aliceHub.SendMediaAsync(chat, [photo], "the view");

        Assert.Equal([PushDispatcher.AttachmentPreview, "the view"], api.Push.To(token).Select(p => p.Message.Body));
    }
}
